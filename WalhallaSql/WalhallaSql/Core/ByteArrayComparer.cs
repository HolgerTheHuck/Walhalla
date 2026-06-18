using System;
using System.Collections.Generic;

namespace WalhallaSql.Core;

internal sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
{
    public static ByteArrayComparer Instance { get; } = new();

    public int Compare(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int len = Math.Min(x.Length, y.Length);
        for (int i = 0; i < len; i++)
        {
            int cmp = x[i].CompareTo(y[i]);
            if (cmp != 0) return cmp;
        }
        return x.Length.CompareTo(y.Length);
    }

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        if (x.Length != y.Length) return false;
        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[]? obj)
    {
        if (obj is null) return 0;
        int hash = 0;
        foreach (byte b in obj)
            hash = (hash * 31) ^ b;
        return hash;
    }
}
