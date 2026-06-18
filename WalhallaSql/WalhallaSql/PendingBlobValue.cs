using System;
using System.IO;

namespace WalhallaSql;

internal sealed class PendingBlobValue
{
    private readonly byte[]? _buffer;
    private readonly int _offset;
    private readonly int _length;
    private readonly Func<Stream>? _factory;

    /// <summary>
    /// Optional BlobRef for out-of-line values. When set, the blob is already
    /// stored in a sidecar and can be reused on re-encode without re-appending.
    /// </summary>
    internal readonly Storage.BlobRef? BlobRef;

    /// <summary>
    /// Creates a sentinel backed by a slice of an existing <paramref name="buffer"/>.
    /// No bytes are copied.
    /// </summary>
    public PendingBlobValue(byte[] buffer, int offset, int length)
    {
        _buffer = buffer;
        _offset = offset;
        _length = length;
    }

    /// <summary>
    /// Creates a sentinel backed by a stream factory (e.g. memory-mapped sidecar).
    /// The factory is invoked once per <see cref="OpenStream"/> call.
    /// </summary>
    public PendingBlobValue(Func<Stream> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Creates a sentinel backed by a sidecar BlobRef. The factory is used for
    /// streaming; the BlobRef is retained for re-encode reuse.
    /// </summary>
    public PendingBlobValue(Func<Stream> factory, Storage.BlobRef blobRef) : this(factory)
    {
        BlobRef = blobRef;
    }

    /// <summary>The number of bytes in the blob.</summary>
    public int Length => _length;

    /// <summary>
    /// Opens a <see cref="Stream"/> for reading the blob without allocating a
    /// separate <c>byte[]</c>.  Callers are responsible for disposing the stream.
    /// </summary>
    public Stream OpenStream()
    {
        if (_factory != null)
            return _factory();

        return new MemoryStream(_buffer!, _offset, _length, writable: false);
    }

    /// <summary>
    /// Materialises the blob into a new <c>byte[]</c>.  Use only when a
    /// contiguous array is strictly required (e.g. <c>GetBytes</c>).
    /// </summary>
    public byte[] ToArray()
    {
        if (_factory != null)
        {
            using var s = _factory();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        if (_offset == 0 && _length == _buffer!.Length)
            return _buffer;

        var result = new byte[_length];
        Buffer.BlockCopy(_buffer!, _offset, result, 0, _length);
        return result;
    }
}
