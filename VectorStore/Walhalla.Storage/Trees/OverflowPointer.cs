// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Encodiert die Position eines out-of-line Blob im Overflow-Store.
/// 16 Bytes: Offset (int64) + Length (int32) + CRC32 (uint32).
/// </summary>
internal readonly struct OverflowPointer
{
    public const int SizeInBytes = sizeof(long) + sizeof(int) + sizeof(uint); // 16

    public long Offset { get; }
    public int Length { get; }
    public uint Crc { get; }

    public OverflowPointer(long offset, int length)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Offset = offset;
        Length = length;
        Crc = ComputeCrc(offset, length);
    }

    private OverflowPointer(long offset, int length, uint crc)
    {
        Offset = offset;
        Length = length;
        Crc = crc;
    }

    public byte[] Encode()
    {
        var buf = new byte[SizeInBytes];
        BinaryPrimitives.WriteInt64LittleEndian(buf, Offset);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), Crc);
        return buf;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out OverflowPointer ptr)
    {
        if (data.Length != SizeInBytes)
        {
            ptr = default;
            return false;
        }

        var offset = BinaryPrimitives.ReadInt64LittleEndian(data);
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8));
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12));

        if (crc != ComputeCrc(offset, length))
        {
            ptr = default;
            return false;
        }

        ptr = new OverflowPointer(offset, length, crc);
        return true;
    }

    private static uint ComputeCrc(long offset, int length)
    {
        // Simple deterministic hash — prevents accidental collision with
        // arbitrary 16-byte inline values.
        return (uint)(offset ^ length ^ 0xB10B_B10B);
    }

    public override string ToString() => $"OverflowPointer(Offset={Offset}, Length={Length})";
}
