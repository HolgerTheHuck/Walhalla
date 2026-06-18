using System;

namespace WalhallaSql.Core;

internal static class ByteLexicographicalComparer
{
    public static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var minLength = Math.Min(left.Length, right.Length);
        for (var i = 0; i < minLength; i++)
        {
            var cmp = left[i].CompareTo(right[i]);
            if (cmp != 0) return cmp;
        }
        return left.Length.CompareTo(right.Length);
    }

    public static int Compare(byte[]? left, byte[]? right)
    {
        left ??= Array.Empty<byte>();
        right ??= Array.Empty<byte>();
        return Compare(left.AsSpan(), right.AsSpan());
    }
}
