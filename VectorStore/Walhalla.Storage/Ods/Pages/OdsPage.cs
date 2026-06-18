// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers;

namespace Walhalla.Storage.Ods.Pages;

/// <summary>
/// A page buffer wrapper. When <paramref name="pooled"/> is <see langword="true"/> the
/// underlying byte-array was rented from <see cref="ArrayPool{T}.Shared"/> and will be
/// returned to the pool on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// On-disk page layout (v2):
/// <code>
/// [Header: OdsPageHeader.SizeInBytes bytes]
/// [Body:   PageSize - OdsPageHeader.SizeInBytes - ChecksumSizeInBytes bytes]
/// [FNV-1a: ChecksumSizeInBytes bytes (uint32, little-endian)]
/// </code>
/// The checksum covers all bytes from offset 0 to PageSize-4.
/// </remarks>
internal sealed class OdsPage : IDisposable
{
    /// <summary>Number of bytes reserved at the end of every page for the FNV-1a checksum.</summary>
    public const int ChecksumSizeInBytes = sizeof(uint);

    private readonly int _pageSize;
    private readonly bool _pooled;
    private readonly int _checksumReserveBytes;
    private bool _disposed;

    /// <param name="checksumReserveBytes">
    /// Number of bytes at the end of the page reserved for the checksum and excluded from
    /// <see cref="Body"/>. Pass <c>0</c> when reading legacy V1 pages (no checksum slot).
    /// Defaults to <see cref="ChecksumSizeInBytes"/>.
    /// </param>
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

    public int PageId { get; }

    /// <summary>The raw byte array backing this page (may be larger than the page size when pooled).</summary>
    public byte[] Buffer { get; }

    public OdsPageHeader Header
    {
        get => OdsPageHeader.Read(Buffer);
        set => value.Write(Buffer);
    }

    /// <summary>The page body (everything after the fixed-size header, before the trailing checksum),
    /// exactly <c>PageSize – HeaderSize – checksumReserveBytes</c> bytes.
    /// For legacy V1 pages (<c>checksumReserveBytes = 0</c>) this spans the full post-header area.</summary>
    public Span<byte> Body => Buffer.AsSpan(OdsPageHeader.SizeInBytes, _pageSize - OdsPageHeader.SizeInBytes - _checksumReserveBytes);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_pooled)
            ArrayPool<byte>.Shared.Return(Buffer, clearArray: false);
    }
}
