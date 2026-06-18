// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Encodes the location of a blob payload within <c>blobs.dat</c> as a compact
/// 12-byte sequence suitable for storing as a value in <c>WalhallaStore</c>.
/// </summary>
internal readonly struct BlobPointer
{
    /// <summary>Serialised byte size: 8 (offset) + 4 (length).</summary>
    public const int SizeInBytes = sizeof(long) + sizeof(int);

    /// <summary>Byte offset of the payload from the start of the blob file.</summary>
    public long Offset { get; }

    /// <summary>Exact byte length of the payload.</summary>
    public int Length { get; }

    public BlobPointer(long offset, int length)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Offset = offset;
        Length = length;
    }

    /// <summary>Serialises this pointer to a new 12-byte array (little-endian).</summary>
    public byte[] Encode()
    {
        var buf = new byte[SizeInBytes];
        BinaryPrimitives.WriteInt64LittleEndian(buf, Offset);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(sizeof(long)), Length);
        return buf;
    }

    /// <summary>Deserialises a pointer from a 12-byte span.</summary>
    public static BlobPointer Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < SizeInBytes)
            throw new ArgumentException($"Buffer must be at least {SizeInBytes} bytes.", nameof(buf));

        var offset = BinaryPrimitives.ReadInt64LittleEndian(buf);
        var length = BinaryPrimitives.ReadInt32LittleEndian(buf[sizeof(long)..]);
        return new BlobPointer(offset, length);
    }
}
