using System;
using System.Collections.Generic;

namespace WalhallaSql.Core;

internal sealed class ByteArrayContentComparer : IEqualityComparer<byte[]>
{
    public static ByteArrayContentComparer Instance { get; } = new();

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null) return false;
        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        if (obj == null) return 0;
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var b in obj)
                hash = (hash * 16777619) ^ b;
            return hash;
        }
    }
}
