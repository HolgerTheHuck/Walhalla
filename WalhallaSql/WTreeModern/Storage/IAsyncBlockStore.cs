namespace WTreeModern.Storage;

/// <summary>
/// Async-Pendant zu <see cref="IBlockStore"/u003e.
/// Ermöglicht nicht-blockierende I/O für Node-Loading und Storage-Commit.
/// </summary>
public interface IAsyncBlockStore : IAsyncDisposable
{
    /// <summary>Anzahl der bisher allozierten Handles.</summary>
    long AllocatedCount { get; }

    /// <summary>Reserviert einen neuen eindeutigen Handle.</summary>
    ValueTask<long> AllocateHandleAsync(CancellationToken ct = default);

    /// <summary>Liest den gespeicherten Byte-Inhalt eines Blocks.</summary>
    ValueTask<byte[]> ReadAsync(long handle, CancellationToken ct = default);

    /// <summary>Schreibt (oder überschreibt) den Inhalt eines Blocks.</summary>
    ValueTask WriteAsync(long handle, byte[] data, int offset, int length, CancellationToken ct = default);

    /// <summary>Gibt an ob ein Handle bereits beschrieben wurde.</summary>
    ValueTask<bool> ExistsAsync(long handle, CancellationToken ct = default);

    /// <summary>Leert ausstehende Schreiboperationen atomar auf das Medium.</summary>
    ValueTask CommitAsync(CancellationToken ct = default);

    /// <summary>Schließt den Store sauber (ohne Commit).</summary>
    ValueTask CloseAsync(CancellationToken ct = default);
}
