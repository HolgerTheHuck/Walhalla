using System;
using System.Buffers.Binary;

namespace WalhallaSql.Storage;

/// <summary>
/// Reference to a blob payload stored out-of-line in a <see cref="BlobSidecarFile"/>.
/// Encoded inline into the Row bytes as a 16-byte structure when the payload exceeds
/// the <see cref="WalhallaOptions.BlobInliningThreshold"/>.
/// </summary>
/// <remarks>
/// Detected in RowCodec by a 4-byte length prefix of <c>0xFFFFFFFF</c> (sentinel),
/// followed by this 16-byte structure. All other positive lengths mean inline bytes.
/// </remarks>
internal readonly struct BlobRef : IEquatable<BlobRef>
{
    /// <summary>Serialised byte size: 8 (offset) + 4 (length) + 4 (flags).</summary>
    public const int SizeInBytes = sizeof(long) + sizeof(int) + sizeof(uint);

    /// <summary>Sentinel written as the 4-byte length prefix to signal a BlobRef follows.</summary>
    public const uint Sentinel = 0xFFFFFFFF;

    /// <summary>Byte offset of the payload from the start of the sidecar file.</summary>
    public long Offset { get; }

    /// <summary>Exact byte length of the payload.</summary>
    public int Length { get; }

    /// <summary>Flags (reserved: compression, inline-override, future table-id sharing).</summary>
    public uint Flags { get; }

    public BlobRef(long offset, int length, uint flags = 0)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Offset = offset;
        Length = length;
        Flags = flags;
    }

    /// <summary>Serialises this reference to a new 16-byte array (little-endian).</summary>
    public byte[] Encode()
    {
        var buf = new byte[SizeInBytes];
        BinaryPrimitives.WriteInt64LittleEndian(buf, Offset);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(sizeof(long)), Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sizeof(long) + sizeof(int)), Flags);
        return buf;
    }

    /// <summary>Deserialises a reference from a 16-byte span.</summary>
    public static BlobRef Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < SizeInBytes)
            throw new ArgumentException($"Buffer must be at least {SizeInBytes} bytes.", nameof(buf));

        var offset = BinaryPrimitives.ReadInt64LittleEndian(buf);
        var length = BinaryPrimitives.ReadInt32LittleEndian(buf[sizeof(long)..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf[(sizeof(long) + sizeof(int))..]);
        return new BlobRef(offset, length, flags);
    }

    public bool Equals(BlobRef other) => Offset == other.Offset && Length == other.Length && Flags == other.Flags;
    public override bool Equals(object? obj) => obj is BlobRef other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Offset, Length, Flags);
}
