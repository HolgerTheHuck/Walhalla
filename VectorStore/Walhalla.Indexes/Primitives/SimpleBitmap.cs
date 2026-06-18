// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;

namespace Walhalla.Indexes.Primitives;

/// <summary>
/// Kompakte Bitmap auf Basis von ulong[]-Chunks.
/// Keine externe Abhaengigkeit – fuer embedded .NET optimiert.
/// </summary>
public sealed class SimpleBitmap
{
    private const byte SerializationVersion = 1;

    private ulong[] _bits;
    private int _count;

    public int Count => _count;

    public SimpleBitmap(int initialCapacity = 1024)
    {
        _bits = new ulong[(initialCapacity + 63) >> 6];
        _count = 0;
    }

    private SimpleBitmap(ulong[] bits, int count)
    {
        _bits = bits;
        _count = count;
    }

    /// <summary>Setzt das Bit an Position index auf 1.</summary>
    public void Set(ulong index)
    {
        int chunk = (int)(index >> 6);
        int bit = (int)(index & 63);

        EnsureCapacity(chunk);

        ulong mask = 1UL << bit;
        ulong old = _bits[chunk];
        if ((old & mask) == 0)
        {
            _bits[chunk] = old | mask;
            _count++;
        }
    }

    /// <summary>Prueft, ob das Bit an Position index gesetzt ist.</summary>
    public bool Get(ulong index)
    {
        int chunk = (int)(index >> 6);
        if (chunk >= _bits.Length) return false;
        int bit = (int)(index & 63);
        return (_bits[chunk] & (1UL << bit)) != 0;
    }

    /// <summary>Entfernt das Bit an Position index.</summary>
    public void Clear(ulong index)
    {
        int chunk = (int)(index >> 6);
        if (chunk >= _bits.Length) return;
        int bit = (int)(index & 63);

        ulong mask = 1UL << bit;
        ulong old = _bits[chunk];
        if ((old & mask) != 0)
        {
            _bits[chunk] = old & ~mask;
            _count--;
        }
    }

    /// <summary>Leert die gesamte Bitmap.</summary>
    public void ClearAll()
    {
        Array.Clear(_bits);
        _count = 0;
    }

    /// <summary>Logisches AND mit einer anderen Bitmap.</summary>
    public SimpleBitmap And(SimpleBitmap other)
    {
        int minLen = Math.Min(_bits.Length, other._bits.Length);
        var result = new ulong[minLen];
        int count = 0;

        for (int i = 0; i < minLen; i++)
        {
            result[i] = _bits[i] & other._bits[i];
            count += BitCount(result[i]);
        }

        return new SimpleBitmap(result, count);
    }

    /// <summary>Logisches OR mit einer anderen Bitmap.</summary>
    public SimpleBitmap Or(SimpleBitmap other)
    {
        int maxLen = Math.Max(_bits.Length, other._bits.Length);
        var result = new ulong[maxLen];
        int count = 0;

        for (int i = 0; i < maxLen; i++)
        {
            ulong a = i < _bits.Length ? _bits[i] : 0;
            ulong b = i < other._bits.Length ? other._bits[i] : 0;
            result[i] = a | b;
            count += BitCount(result[i]);
        }

        return new SimpleBitmap(result, count);
    }

    /// <summary>Logisches AND-NOT: Diese Bitmap minus andere Bitmap.</summary>
    public SimpleBitmap AndNot(SimpleBitmap other)
    {
        var result = new ulong[_bits.Length];
        int count = 0;

        int minLen = Math.Min(_bits.Length, other._bits.Length);
        for (int i = 0; i < minLen; i++)
        {
            result[i] = _bits[i] & ~other._bits[i];
            count += BitCount(result[i]);
        }

        // Rest von this kopieren
        for (int i = minLen; i < _bits.Length; i++)
        {
            result[i] = _bits[i];
            count += BitCount(result[i]);
        }

        return new SimpleBitmap(result, count);
    }

    /// <summary>Iteriert ueber alle gesetzten Bits.</summary>
    public IEnumerable<ulong> EnumerateSetBits()
    {
        for (int chunk = 0; chunk < _bits.Length; chunk++)
        {
            ulong word = _bits[chunk];
            if (word == 0) continue;

            ulong baseIndex = (ulong)chunk << 6;
            while (word != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                if (bit >= 64) break;
                yield return baseIndex + (ulong)bit;
                word &= ~(1UL << bit);
            }
        }
    }

    /// <summary>Serialisiert die gesetzten Bits als versionierte Delta-Varint-Liste.</summary>
    public byte[] Serialize()
    {
        var buffer = new List<byte>(Math.Max(2, _count * 2));
        buffer.Add(SerializationVersion);
        WriteVarUInt64(buffer, (ulong)_count);

        ulong previous = 0;
        bool hasPrevious = false;
        foreach (ulong index in EnumerateSetBits())
        {
            ulong delta = hasPrevious ? index - previous : index;
            WriteVarUInt64(buffer, delta);
            previous = index;
            hasPrevious = true;
        }

        return buffer.ToArray();
    }

    /// <summary>Deserialisiert eine mit <see cref="Serialize"/> erzeugte Bitmap.</summary>
    public static SimpleBitmap Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("Bitmap payload is empty.");

        if (data[0] != SerializationVersion)
            throw new InvalidDataException($"Unsupported bitmap payload version {data[0]}.");

        int offset = 1;
        ulong encodedCount = ReadVarUInt64(data, ref offset);
        if (encodedCount > int.MaxValue)
            throw new InvalidDataException("Bitmap payload contains too many entries.");

        var bitmap = new SimpleBitmap((int)Math.Min(Math.Max(encodedCount, 1), 1024));
        ulong previous = 0;
        for (ulong i = 0; i < encodedCount; i++)
        {
            ulong delta = ReadVarUInt64(data, ref offset);
            ulong index = i == 0 ? delta : checked(previous + delta);
            bitmap.Set(index);
            previous = index;
        }

        if (offset != data.Length)
            throw new InvalidDataException("Bitmap payload contains trailing bytes.");

        return bitmap;
    }

    private void EnsureCapacity(int chunk)
    {
        if (chunk < _bits.Length) return;
        int newLen = Math.Max(_bits.Length * 2, chunk + 1);
        Array.Resize(ref _bits, newLen);
    }

    private static int BitCount(ulong value)
    {
        return System.Numerics.BitOperations.PopCount(value);
    }

    private static void WriteVarUInt64(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }

    private static ulong ReadVarUInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        int shift = 0;

        while (offset < data.Length)
        {
            byte current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
                return value;

            shift += 7;
            if (shift >= 64)
                throw new InvalidDataException("Bitmap payload contains an invalid varint.");
        }

        throw new InvalidDataException("Bitmap payload ended unexpectedly.");
    }
}
