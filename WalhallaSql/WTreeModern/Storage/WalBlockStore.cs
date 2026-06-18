using WTreeModern.Diagnostics;

namespace WTreeModern.Storage;

/// <summary>
/// Deferred-WAL-Decorator für einen <see cref="IBlockStore"/>.
///
/// Sicherheitseigenschaft:
///   Schreiboperationen werden im Speicher gepuffert und erst bei
///   <see cref="Commit"/> in eine WAL-Datei geschrieben.  Nach einem Absturz
///   zu einem beliebigen Zeitpunkt ist der innere Store stets im Zustand des
///   letzten erfolgreichen Commit.
///
/// Nutzung:
/// <code>
///   var inner = new FileBlockStore("data.wtree");
///   var store = new WalBlockStore(inner, "data.wtree.wal");
///   var tree  = new WTree&lt;int, string&gt;(store, ...);
/// </code>
///
/// WAL-Dateiformat (binär, Version 1):
///   [8 Bytes]  Magic 0x57414C303031 ("WAL001")
///   [4 Bytes]  Version 1
///   [8 Bytes]  Anzahl der Einträge
///   Pro Eintrag:
///     [8 Bytes] Handle
///     [4 Bytes] Länge
///     [N Bytes] Daten
/// </summary>
public sealed class WalBlockStore : IBlockStore, IAsyncBlockStore
{
    // "WAL001" als little-endian Int64
    internal const long WAL_MAGIC = 0x3130_304C_4157_5457L;
    internal const int WAL_VERSION = 2;
    internal const int WAL_HEADER_SIZE = 8 + 4 + 8; // magic + version + count
    internal const int WAL_ENTRY_HEADER = 8 + 4;      // handle + length
    internal const int WAL_CRC_SIZE = 4;              // CRC32 at end

    private readonly IBlockStore _inner;
    private readonly IAsyncBlockStore _asyncInner;
    private readonly string _walPath;
    private readonly string _walTempPath;
    private readonly ILogger _logger;
    private readonly WTreeSyncMode _syncMode;

    // In-memory Puffer der laufenden Transaktion
    private readonly Dictionary<long, byte[]> _pending = new();

    private bool _disposed;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Konstruktor ──────────────────────────────────────────────────────────

    /// <param name="inner">Zu schützender Store (z.B. FileBlockStore).</param>
    /// <param name="walPath">Pfad zur WAL-Datei (typisch: Datenpfad + ".wal").</param>
    /// <param name="logger">Optionaler Logger.</param>
    /// <param name="syncMode">Sync-Modus. Default: <see cref="Adapter.WTreeSyncMode.Full"/>.</param>
    public WalBlockStore(IBlockStore inner, string walPath, ILogger? logger = null,
        WTreeSyncMode syncMode = WTreeSyncMode.Full)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _asyncInner = inner as IAsyncBlockStore ?? new SyncBlockStoreAdapter(inner);
        _walPath = walPath ?? throw new ArgumentNullException(nameof(walPath));
        _walTempPath = walPath + ".tmp";
        _logger = logger ?? NoOpLogger.Instance;
        _syncMode = syncMode;
        RecoverIfNeeded();
    }

    // ── IBlockStore (sync) ───────────────────────────────────────────────────

    public long AllocatedCount => _inner.AllocatedCount;

    public long AllocateHandle() => _inner.AllocateHandle();

    public bool Exists(long handle)
    {
        _lock.Wait();
        try
        {
            return _pending.ContainsKey(handle) || _inner.Exists(handle);
        }
        finally { _lock.Release(); }
    }

    public byte[] Read(long handle)
    {
        _lock.Wait();
        try
        {
            if (_pending.TryGetValue(handle, out var pending))
                return pending;
            return _inner.Read(handle);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Puffert den Schreibvorgang nur in-memory.  Die Daten landen erst bei
    /// <see cref="Commit"/> im WAL und anschließend im inneren Store.
    /// </summary>
    public void Write(long handle, byte[] data, int offset, int length)
    {
        _lock.Wait();
        try
        {
            var buf = new byte[length];
            Buffer.BlockCopy(data, offset, buf, 0, length);
            _pending[handle] = buf;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Schreibt alle gepufferten Einträge in das WAL (fsync), überträgt sie
    /// dann in den inneren Store, committed diesen und löscht das WAL.
    /// </summary>
    public void Commit()
    {
        _lock.Wait();
        try
        {
            if (_pending.Count == 0)
            {
                _inner.Commit();
                return;
            }

            // 1. WAL mit allen pending Einträgen schreiben und fsync
            FlushWal();

            // 2. Writes auf Inner-Store anwenden
            foreach (var (handle, data) in _pending)
                _inner.Write(handle, data, 0, data.Length);

            // 3. Inner-Store atomar persistieren
            _inner.Commit();

            // 4. WAL löschen (Transaktion ist jetzt im inneren Store)
            DeleteWalSafe();
            _pending.Clear();
        }
        finally { _lock.Release(); }
    }

    public void Close()
    {
        _lock.Wait();
        try
        {
            if (!_disposed)
            {
                _inner.Close();
                _disposed = true;
            }
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    // ── IAsyncBlockStore ───────────────────────────────────────────────────

    public async ValueTask<long> AllocateHandleAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _inner.AllocateHandle();
        }
        finally { _lock.Release(); }
    }

    public async ValueTask<bool> ExistsAsync(long handle, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _pending.ContainsKey(handle) || await _asyncInner.ExistsAsync(handle, ct);
        }
        finally { _lock.Release(); }
    }

    public async ValueTask<byte[]> ReadAsync(long handle, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_pending.TryGetValue(handle, out var pending))
                return pending;
            return await _asyncInner.ReadAsync(handle, ct);
        }
        finally { _lock.Release(); }
    }

    public async ValueTask WriteAsync(long handle, byte[] data, int offset, int length, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var buf = new byte[length];
            Buffer.BlockCopy(data, offset, buf, 0, length);
            _pending[handle] = buf;
        }
        finally { _lock.Release(); }
    }

    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_pending.Count == 0)
            {
                await _asyncInner.CommitAsync(ct);
                return;
            }

            // 1. WAL schreiben und fsync
            await FlushWalAsync(ct);

            // 2. Writes auf Inner-Store anwenden
            foreach (var (handle, data) in _pending)
                _inner.Write(handle, data, 0, data.Length);

            // 3. Inner-Store atomar persistieren
            await _asyncInner.CommitAsync(ct);

            // 4. WAL löschen
            DeleteWalSafe();
            _pending.Clear();
        }
        finally { _lock.Release(); }
    }

    public async ValueTask CloseAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_disposed)
            {
                await _asyncInner.CloseAsync(ct);
                _disposed = true;
            }
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }

    // ── WAL Hilfsmethoden ──────────────────────────────────────────────────────

    private void FlushWal()
    {
        using var ms = new MemoryStream();

        // Header
        ms.Write(BitConverter.GetBytes(WAL_MAGIC), 0, 8);
        ms.Write(BitConverter.GetBytes(WAL_VERSION), 0, 4);
        ms.Write(BitConverter.GetBytes((long)_pending.Count), 0, 8);

        // Entries
        foreach (var (handle, data) in _pending)
        {
            ms.Write(BitConverter.GetBytes(handle), 0, 8);
            ms.Write(BitConverter.GetBytes(data.Length), 0, 4);
            ms.Write(data, 0, data.Length);
        }

        // CRC32 über alles außer dem CRC-Feld selbst
        uint crc = Crc32.Compute(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        ms.Write(BitConverter.GetBytes(crc), 0, WAL_CRC_SIZE);

        var walBytes = ms.ToArray();

        // Atomar: erst in tmp schreiben, dann umbenennen
        var fileOptions = _syncMode != WTreeSyncMode.None
            ? FileOptions.WriteThrough
            : FileOptions.None;
        using (var fs = new FileStream(
            _walTempPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, fileOptions))
        {
            fs.Write(walBytes, 0, walBytes.Length);
            if (_syncMode == WTreeSyncMode.Full)
                fs.Flush(true);
            else
                fs.Flush();
        }

        File.Move(_walTempPath, _walPath, overwrite: true);
    }

    private async ValueTask FlushWalAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();

        ms.Write(BitConverter.GetBytes(WAL_MAGIC), 0, 8);
        ms.Write(BitConverter.GetBytes(WAL_VERSION), 0, 4);
        ms.Write(BitConverter.GetBytes((long)_pending.Count), 0, 8);

        foreach (var (handle, data) in _pending)
        {
            ms.Write(BitConverter.GetBytes(handle), 0, 8);
            ms.Write(BitConverter.GetBytes(data.Length), 0, 4);
            ms.Write(data, 0, data.Length);
        }

        uint crc = Crc32.Compute(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        ms.Write(BitConverter.GetBytes(crc), 0, WAL_CRC_SIZE);

        var walBytes = ms.ToArray();

        using (var fs = new FileStream(
            _walTempPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536,
            _syncMode != WTreeSyncMode.None
                ? FileOptions.WriteThrough | FileOptions.Asynchronous
                : FileOptions.Asynchronous))
        {
            await fs.WriteAsync(walBytes, 0, walBytes.Length, ct);
            if (_syncMode == WTreeSyncMode.Full)
                await fs.FlushAsync(ct);
        }

        File.Move(_walTempPath, _walPath, overwrite: true);
    }

    private void DeleteWalSafe()
    {
        try { File.Delete(_walPath); }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warning, $"Failed to delete WAL {_walPath}.", ex);
        }
    }

    // ── Recovery ───────────────────────────────────────────────────────────────

    private void RecoverIfNeeded()
    {
        if (!File.Exists(_walPath)) return;

        _logger.Log(LogLevel.Info, $"WAL recovery started for {_walPath}");

        byte[] walBytes;
        try
        {
            walBytes = File.ReadAllBytes(_walPath);
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warning, "WAL read failed – rolling back.", ex);
            DeleteWalSafe();
            return;
        }

        if (walBytes.Length < WAL_HEADER_SIZE + WAL_CRC_SIZE)
        {
            _logger.Log(LogLevel.Warning, "WAL truncated (too short) – rolling back.");
            DeleteWalSafe();
            return;
        }

        uint storedCrc = BitConverter.ToUInt32(walBytes, walBytes.Length - WAL_CRC_SIZE);
        uint computedCrc = Crc32.Compute(walBytes.AsSpan(0, walBytes.Length - WAL_CRC_SIZE));
        if (storedCrc != computedCrc)
        {
            _logger.Log(LogLevel.Warning,
                $"WAL CRC mismatch (stored={storedCrc:X8}, computed={computedCrc:X8}) – rolling back.");
            DeleteWalSafe();
            return;
        }

        int pos = 0;
        long magic = BitConverter.ToInt64(walBytes, pos); pos += 8;
        if (magic != WAL_MAGIC)
        {
            _logger.Log(LogLevel.Warning, "WAL magic mismatch – discarding.");
            DeleteWalSafe();
            return;
        }

        int version = BitConverter.ToInt32(walBytes, pos); pos += 4;
        if (version != WAL_VERSION)
        {
            _logger.Log(LogLevel.Warning, $"WAL version mismatch ({version}) – discarding.");
            DeleteWalSafe();
            return;
        }

        long count = BitConverter.ToInt64(walBytes, pos); pos += 8;
        var writes = new Dictionary<long, byte[]>();

        try
        {
            for (long i = 0; i < count; i++)
            {
                if (pos + WAL_ENTRY_HEADER > walBytes.Length - WAL_CRC_SIZE)
                    throw new EndOfStreamException("WAL entry header truncated.");

                long handle = BitConverter.ToInt64(walBytes, pos); pos += 8;
                int len = BitConverter.ToInt32(walBytes, pos); pos += 4;

                if (pos + len > walBytes.Length - WAL_CRC_SIZE)
                    throw new EndOfStreamException("WAL entry data truncated.");

                byte[] data = new byte[len];
                Buffer.BlockCopy(walBytes, pos, data, 0, len);
                pos += len;
                writes[handle] = data;
            }
        }
        catch (EndOfStreamException ex)
        {
            _logger.Log(LogLevel.Warning, "WAL truncated – rolling back.", ex);
            DeleteWalSafe();
            return;
        }

        if (writes.Count > 0)
        {
            _logger.Log(LogLevel.Info, $"WAL redo: {writes.Count} writes.");

            long maxHandle = writes.Keys.Max();
            long allocated = _inner.AllocatedCount;
            while (allocated <= maxHandle)
                allocated = _inner.AllocateHandle() + 1;

            foreach (var (h, data) in writes)
                _inner.Write(h, data, 0, data.Length);

            _inner.Commit();
        }
        else
        {
            _logger.Log(LogLevel.Info, "WAL empty – nothing to recover.");
        }

        DeleteWalSafe();
    }
}
