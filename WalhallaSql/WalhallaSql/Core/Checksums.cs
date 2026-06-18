using System;

namespace WalhallaSql.Core;

internal static class Checksums
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    public static uint Fnva32(ReadOnlySpan<byte> data)
    {
        return Fnva32Update(FnvOffset, data);
    }

    public static uint Fnva32Begin() => FnvOffset;

    public static uint Fnva32Update(uint hash, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            hash ^= b;
            hash *= FnvPrime;
        }
        return hash;
    }
}
