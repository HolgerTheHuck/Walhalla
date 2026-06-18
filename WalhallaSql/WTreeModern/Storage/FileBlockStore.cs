using System.IO.MemoryMappedFiles;

namespace WTreeModern.Storage;

/// <summary>
/// Dateibasierter BlockStore mit nativem async-I/O.
///
/// Dateiformat:
///   [0..7]   Magic "WTREE100" (8 Bytes)
///   [8..15]  Anzahl der belegten Handles (Int64)
///   Danach N Einträge im Index:
///     pro Handle: [offset: Int64][length: Int32]
///   Daten-Bereich wächst ans Ende der Datei.
/// </summary>
public sealed class FileBlockStore : IBlockStore, IAsyncBlockStore
{
    private const long MAGIC = 0x3030_3145_5245_5457L; // "WTREE100"
    private const int HEADER_SIZE = 16;   // magic(8) + count(8)
    private const int INDEX_ENTRY = 12;   // offset(8) + length(4)

    private readonly string _path;
    private FileStream _file;
    private readonly bool _useMmap;
    private readonly WTreeSyncMode _syncMode;

    // Optionaler Memory-Mapped-File-Cache für reads
    private MemoryMappedFile? _mmapFile;
    private long _mmapLength;

    // In-memory Repräsentation des Index
    private readonly List<(long Offset, int Length)> _index = [];

    // Ausstehende Schreiboperationen (handle → data)
    private readonly Dictionary<long, byte[]> _pending = [];

    private long _nextHandle;
    private bool _disposed;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileBlockStore(string path, bool useMmap = false,
        WTreeSyncMode syncMode = WTreeSyncMode.Full)
    {
        _path = path;
        _useMmap = useMmap;
        _syncMode = syncMode;

        if (File.Exists(path))
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536, FileOptions.RandomAccess | FileOptions.Asynchronous);
            LoadIndex();
        }
        else
        {
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536, FileOptions.RandomAccess | FileOptions.Asynchronous);
            WriteEmptyHeader();
        }
    }

    // ── IBlockStore (sync) ───────────────────────────────────────────────────

    public long AllocatedCount => _nextHandle;

    public long AllocateHandle()
    {
        _lock.Wait();
        try
        {
            long h = _nextHandle++;
            _index.Add((0, 0));
            return h;
        }
        finally { _lock.Release(); }
    }

    public bool Exists(long handle)
    {
        _lock.Wait();
        try
        {
            return handle < _index.Count && _index[(int)handle].Length > 0;
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

            var (offset, length) = _index[(int)handle];
            if (length == 0)
                throw new InvalidOperationException($"Block {handle} wurde noch nicht beschrieben.");

            var buf = new byte[length];
            if (_useMmap)
                MmapRead(offset, buf);
            else
            {
                _file.Seek(offset, SeekOrigin.Begin);
                _file.ReadExactly(buf);
            }
            return buf;
        }
        finally { _lock.Release(); }
    }

    private void MmapRead(long offset, byte[] buf)
    {
        EnsureMmap();
        using var view = _mmapFile!.CreateViewAccessor(offset, buf.Length, MemoryMappedFileAccess.Read);
        view.ReadArray(0, buf, 0, buf.Length);
    }

    private void EnsureMmap()
    {
        long len = _file.Length;
        if (_mmapFile != null && _mmapLength >= len) return;

        _mmapFile?.Dispose();
        _mmapFile = MemoryMappedFile.CreateFromFile(_file, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
        _mmapLength = len;
    }

    private void InvalidateMmap()
    {
        _mmapFile?.Dispose();
        _mmapFile = null;
        _mmapLength = 0;
    }

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

    public void Commit()
    {
        _lock.Wait();
        try
        {
            CommitCore();
        }
        finally { _lock.Release(); }
    }

    private void CommitCore()
    {
        if (_pending.Count == 0)
            return;

        long minDataOffset = HEADER_SIZE + (long)_index.Count * INDEX_ENTRY;

        // Any previously-committed block whose data starts within [0, minDataOffset) would be
        // overwritten when FlushIndex writes the now-larger index header. Read and relocate
        // those blocks before writing the index.
        var toRelocate = new List<(int Handle, byte[] Data)>();
        for (int i = 0; i < _index.Count; i++)
        {
            var (offset, length) = _index[i];
            if (length > 0 && offset < minDataOffset && !_pending.ContainsKey(i))
            {
                var buf = new byte[length];
                _file.Seek(offset, SeekOrigin.Begin);
                _file.ReadExactly(buf);
                toRelocate.Add((i, buf));
            }
        }

        long writePos = Math.Max(_file.Length, minDataOffset);
        _file.Seek(writePos, SeekOrigin.Begin);

        foreach (var (handle, data) in toRelocate)
        {
            long fileOffset = _file.Position;
            _file.Write(data, 0, data.Length);
            _index[handle] = (fileOffset, data.Length);
        }

        foreach (var (handle, data) in _pending)
        {
            long fileOffset = _file.Position;
            _file.Write(data, 0, data.Length);
            _index[(int)handle] = (fileOffset, data.Length);
        }

        _pending.Clear();
        FlushIndex();
        if (_syncMode != WTreeSyncMode.None)
            _file.Flush(true);
        else
            _file.Flush();
        InvalidateMmap();
    }

    private void FlushIndex()
    {
        long count = _index.Count;
        _file.Seek(0, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[16];
        BitConverter.TryWriteBytes(header, MAGIC);
        BitConverter.TryWriteBytes(header.Slice(8), count);
        _file.Write(header);

        var indexBuf = new byte[count * INDEX_ENTRY];
        for (int i = 0; i < count; i++)
        {
            var (off, len) = _index[i];
            BitConverter.TryWriteBytes(indexBuf.AsSpan(i * INDEX_ENTRY), off);
            BitConverter.TryWriteBytes(indexBuf.AsSpan(i * INDEX_ENTRY + 8), len);
        }
        _file.Write(indexBuf, 0, indexBuf.Length);
    }

    public void Close()
    {
        _lock.Wait();
        try
        {
            if (!_disposed)
            {
                InvalidateMmap();
                _file.Close();
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
            long h = _nextHandle++;
            _index.Add((0, 0));
            return h;
        }
        finally { _lock.Release(); }
    }

    public async ValueTask<bool> ExistsAsync(long handle, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return handle < _index.Count && _index[(int)handle].Length > 0;
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

            var (offset, length) = _index[(int)handle];
            if (length == 0)
                throw new InvalidOperationException($"Block {handle} wurde noch nicht beschrieben.");

            var buf = new byte[length];
            _file.Seek(offset, SeekOrigin.Begin);
            await _file.ReadExactlyAsync(buf, ct);
            return buf;
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
            await CommitCoreAsync(ct);
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
                InvalidateMmap();
                await _file.DisposeAsync();
                _disposed = true;
            }
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }

    // ── private Hilfsmethoden ────────────────────────────────────────────────

    private void LoadIndex()
    {
        var headerBuf = new byte[HEADER_SIZE];
        _file.Seek(0, SeekOrigin.Begin);
        _file.ReadExactly(headerBuf);

        long magic = BitConverter.ToInt64(headerBuf, 0);
        if (magic != MAGIC)
            throw new InvalidDataException("Ungültiges WTreeModern-Dateiformat.");

        long count = BitConverter.ToInt64(headerBuf, 8);
        _nextHandle = count;

        var indexBuf = new byte[count * INDEX_ENTRY];
        _file.ReadExactly(indexBuf);

        for (long i = 0; i < count; i++)
        {
            long off = BitConverter.ToInt64(indexBuf, (int)(i * INDEX_ENTRY));
            int  len = BitConverter.ToInt32(indexBuf, (int)(i * INDEX_ENTRY + 8));
            _index.Add((off, len));
        }
    }

    private void WriteEmptyHeader()
    {
        _file.Seek(0, SeekOrigin.Begin);
        _file.Write(BitConverter.GetBytes(MAGIC));
        _file.Write(BitConverter.GetBytes(0L));
        _file.Flush();
    }

    private async ValueTask CommitCoreAsync(CancellationToken ct = default)
    {
        if (_pending.Count == 0)
            return;

        long minDataOffset = HEADER_SIZE + (long)_index.Count * INDEX_ENTRY;

        var toRelocate = new List<(int Handle, byte[] Data)>();
        for (int i = 0; i < _index.Count; i++)
        {
            var (offset, length) = _index[i];
            if (length > 0 && offset < minDataOffset && !_pending.ContainsKey(i))
            {
                var buf = new byte[length];
                _file.Seek(offset, SeekOrigin.Begin);
                await _file.ReadExactlyAsync(buf, ct);
                toRelocate.Add((i, buf));
            }
        }

        long writePos = Math.Max(_file.Length, minDataOffset);
        _file.Seek(writePos, SeekOrigin.Begin);

        foreach (var (handle, data) in toRelocate)
        {
            long fileOffset = _file.Position;
            await _file.WriteAsync(data, ct);
            _index[handle] = (fileOffset, data.Length);
        }

        foreach (var (handle, data) in _pending)
        {
            long fileOffset = _file.Position;
            await _file.WriteAsync(data, ct);
            _index[(int)handle] = (fileOffset, data.Length);
        }

        _pending.Clear();
        await FlushIndexAsync(ct);
        if (_syncMode != WTreeSyncMode.None)
            await _file.FlushAsync(ct);
        InvalidateMmap();
    }

    private async ValueTask FlushIndexAsync(CancellationToken ct = default)
    {
        long count = _index.Count;
        _file.Seek(0, SeekOrigin.Begin);

        Span<byte> magic = stackalloc byte[8];
        BitConverter.TryWriteBytes(magic, MAGIC);
        await _file.WriteAsync(magic.ToArray(), 0, 8, ct);

        Span<byte> countBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(countBytes, count);
        await _file.WriteAsync(countBytes.ToArray(), 0, 8, ct);

        var indexBuf = new byte[count * INDEX_ENTRY];
        for (int i = 0; i < count; i++)
        {
            var (off, len) = _index[i];
            BitConverter.TryWriteBytes(indexBuf.AsSpan(i * INDEX_ENTRY), off);
            BitConverter.TryWriteBytes(indexBuf.AsSpan(i * INDEX_ENTRY + 8), len);
        }
        await _file.WriteAsync(indexBuf, 0, indexBuf.Length, ct);
    }
}
