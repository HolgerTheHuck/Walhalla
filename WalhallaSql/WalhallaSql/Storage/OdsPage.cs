using System;
using System.Buffers;

namespace WalhallaSql.Storage;

internal sealed class OdsPage : IDisposable
{
    public const int ChecksumSizeInBytes = sizeof(uint);

    private readonly int _pageSize;
    private readonly bool _pooled;
    private readonly int _checksumReserveBytes;
    private bool _disposed;

    public OdsPage(int pageId, byte[] buffer, int pageSize, bool pooled = false,
                   int checksumReserveBytes = ChecksumSizeInBytes)
    {
        PageId = pageId;
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        if (buffer.Length < pageSize)
            throw new ArgumentException("Buffer is smaller than the declared page size.", nameof(buffer));
        _pageSize = pageSize;
        _pooled = pooled;
        _checksumReserveBytes = checksumReserveBytes;
    }

    public int PageId { get; private set; }
    public byte[] Buffer { get; }

    public OdsPageHeader Header
    {
        get => OdsPageHeader.Read(Buffer);
        set => value.Write(Buffer);
    }

    public Span<byte> Body => Buffer.AsSpan(
        OdsPageHeader.SizeInBytes,
        _pageSize - OdsPageHeader.SizeInBytes - _checksumReserveBytes);

    /// <summary>Reinitialize this page for a different page ID without re-allocating the buffer.</summary>
    public void Reuse(int pageId)
    {
        if (!_pooled)
            throw new InvalidOperationException("Only pooled pages can be reused.");
        _disposed = false;
        PageId = pageId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pooled)
            ArrayPool<byte>.Shared.Return(Buffer, clearArray: false);
    }
}
