namespace WTreeModern.Storage;

/// <summary>
/// Adapter von <see cref="IBlockStore"/u003e zu <see cref="IAsyncBlockStore"/u003e.
/// Alle async-Methoden laufen synchron ab (schnelle Completion),
/// da der umschlossene Store kein echtes async-I/O unterstützt.
/// </summary>
public sealed class SyncBlockStoreAdapter : IAsyncBlockStore
{
    private readonly IBlockStore _inner;

    public SyncBlockStoreAdapter(IBlockStore inner)
    {
        _inner = inner;
    }

    public long AllocatedCount => _inner.AllocatedCount;

    public ValueTask<long> AllocateHandleAsync(CancellationToken ct = default)
        => new(_inner.AllocateHandle());

    public ValueTask<bool> ExistsAsync(long handle, CancellationToken ct = default)
        => new(_inner.Exists(handle));

    public ValueTask<byte[]> ReadAsync(long handle, CancellationToken ct = default)
        => new(_inner.Read(handle));

    public ValueTask WriteAsync(long handle, byte[] data, int offset, int length, CancellationToken ct = default)
    {
        _inner.Write(handle, data, offset, length);
        return default;
    }

    public ValueTask CommitAsync(CancellationToken ct = default)
    {
        _inner.Commit();
        return default;
    }

    public ValueTask CloseAsync(CancellationToken ct = default)
    {
        _inner.Close();
        return default;
    }

    public ValueTask DisposeAsync()
    {
        _inner.Dispose();
        return default;
    }
}
