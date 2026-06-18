using System.Collections.Concurrent;

namespace WTreeModern.Storage;

/// <summary>
/// Rein speicherbasierter BlockStore – ideal für Tests und
/// für Szenarien, bei denen Persistenz nicht benötigt wird.
/// Thread-safe.
/// </summary>
public sealed class MemoryBlockStore : IBlockStore
{
    private readonly ConcurrentDictionary<long, byte[]> _blocks = new();
    private long _nextHandle = 0;

    public long AllocatedCount => Interlocked.Read(ref _nextHandle);

    public long AllocateHandle() => Interlocked.Increment(ref _nextHandle) - 1;

    public bool Exists(long handle) => _blocks.ContainsKey(handle);

    public byte[] Read(long handle)
    {
        if (_blocks.TryGetValue(handle, out var data))
            return data;
        throw new KeyNotFoundException($"Block {handle} nicht gefunden.");
    }

    public void Write(long handle, byte[] data, int offset, int length)
    {
        var buf = new byte[length];
        Buffer.BlockCopy(data, offset, buf, 0, length);
        _blocks[handle] = buf;
    }

    public void Commit() { /* nichts zu tun – alles im RAM */ }
    public void Close() { }
    public void Dispose() { GC.SuppressFinalize(this); }

    // ── IAsyncBlockStore ────────────────────────────────────────────────────

    public ValueTask<long> AllocateHandleAsync(CancellationToken ct = default)
        => new(AllocateHandle());

    public ValueTask<bool> ExistsAsync(long handle, CancellationToken ct = default)
        => new(Exists(handle));

    public ValueTask<byte[]> ReadAsync(long handle, CancellationToken ct = default)
        => new(Read(handle));

    public ValueTask WriteAsync(long handle, byte[] data, int offset, int length, CancellationToken ct = default)
    {
        Write(handle, data, offset, length);
        return default;
    }

    public ValueTask CommitAsync(CancellationToken ct = default)
        => default;

    public ValueTask CloseAsync(CancellationToken ct = default)
        => default;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
