using System;

namespace WalhallaSql.Storage;

internal sealed class BloomFilter
{
    private readonly byte[] _bits;
    private readonly int _bitCount;
    private readonly int _hashCount;

    public BloomFilter(int expectedItems, double falsePositiveRate = 0.01)
    {
        if (expectedItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedItems));
        if (falsePositiveRate is <= 0d or >= 1d)
            throw new ArgumentOutOfRangeException(nameof(falsePositiveRate));

        double ln2 = Math.Log(2);
        double m = -(expectedItems * Math.Log(falsePositiveRate)) / (ln2 * ln2);
        _bitCount = (int)Math.Ceiling(m);
        _bits = new byte[(_bitCount + 7) >> 3];
        _hashCount = Math.Max(1, (int)Math.Round((_bitCount / (double)expectedItems) * ln2));
    }

    public void Add(byte[] key)
    {
        Hash(key, out uint h1, out uint h2);
        for (int i = 0; i < _hashCount; i++)
        {
            int pos = (int)((h1 + (uint)i * h2) % (uint)_bitCount);
            _bits[pos >> 3] |= (byte)(1 << (pos & 7));
        }
    }

    public bool MightContain(byte[] key)
    {
        Hash(key, out uint h1, out uint h2);
        for (int i = 0; i < _hashCount; i++)
        {
            int pos = (int)((h1 + (uint)i * h2) % (uint)_bitCount);
            if ((_bits[pos >> 3] & (byte)(1 << (pos & 7))) == 0)
                return false;
        }
        return true;
    }

    public void Clear() => Array.Clear(_bits, 0, _bits.Length);

    private static void Hash(byte[] data, out uint h1, out uint h2)
    {
        const ulong Prime = 1099511628211UL;
        const ulong Offset = 14695981039346656037UL;

        ulong hash = Offset;
        for (int i = 0; i < data.Length; i++)
            hash = (hash ^ data[i]) * Prime;

        h1 = (uint)(hash >> 32);
        h2 = (uint)hash | 1u;
    }
}
