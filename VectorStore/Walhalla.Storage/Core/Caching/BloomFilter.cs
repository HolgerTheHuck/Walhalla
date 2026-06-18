// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Walhalla.Storage.Core.Caching;

/// <summary>
/// A space-efficient probabilistic data structure that answers membership queries in O(1).
/// <para>
/// False positives are possible (a key that was never added may be reported as present),
/// but false negatives are not (a key that was added is always reported as present).
/// </para>
/// <para>
/// Not thread-safe — callers must hold the store's read/write lock.
/// </para>
/// </summary>
internal sealed class BloomFilter
{
    private readonly byte[] _bits;
    private readonly int    _bitCount;
    private readonly int    _hashCount;

    /// <param name="expectedItems">Expected number of distinct items to be added. Must be positive.</param>
    /// <param name="falsePositiveRate">Desired maximum false-positive probability. Default: 1 %.</param>
    public BloomFilter(int expectedItems, double falsePositiveRate = 0.01)
    {
        if (expectedItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedItems));
        if (falsePositiveRate is <= 0d or >= 1d)
            throw new ArgumentOutOfRangeException(nameof(falsePositiveRate));

        // m = -(n · ln p) / (ln 2)²
        double ln2 = Math.Log(2);
        double m   = -(expectedItems * Math.Log(falsePositiveRate)) / (ln2 * ln2);
        _bitCount  = (int)Math.Ceiling(m);
        _bits      = new byte[(_bitCount + 7) >> 3];

        // k = (m / n) · ln 2  — optimal number of hash functions
        _hashCount = Math.Max(1, (int)Math.Round((_bitCount / (double)expectedItems) * ln2));
    }

    /// <summary>Records that <paramref name="key"/> is a member of the set.</summary>
    public void Add(byte[] key)
    {
        Hash(key, out uint h1, out uint h2);
        for (int i = 0; i < _hashCount; i++)
        {
            int pos = (int)((h1 + (uint)i * h2) % (uint)_bitCount);
            _bits[pos >> 3] |= (byte)(1 << (pos & 7));
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="key"/> might have been added, or
    /// <see langword="false"/> if it was definitely never added.
    /// </summary>
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

    /// <summary>Resets the filter to the empty state without reallocating memory.</summary>
    public void Clear() => Array.Clear(_bits, 0, _bits.Length);

    // Kirsch-Mitzenmacher double-hashing: one 64-bit FNV-1a pass whose result
    // is split into two independent 32-bit seeds h1 and h2.
    private static void Hash(byte[] data, out uint h1, out uint h2)
    {
        const ulong Prime  = 1099511628211UL;
        const ulong Offset = 14695981039346656037UL;

        ulong hash = Offset;
        for (int i = 0; i < data.Length; i++)
            hash = (hash ^ data[i]) * Prime;

        h1 = (uint)(hash >> 32);
        h2 = (uint)hash | 1u; // ensure h2 is odd — coprime with any 2^k modulus
    }
}
