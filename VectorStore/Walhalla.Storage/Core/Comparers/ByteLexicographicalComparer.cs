// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Walhalla.Storage.Core;

/// <summary>
/// Lightweight utility for unsigned lexicographic comparison of raw byte sequences.
/// Matches the ordering used by the default "bytewise" key comparator so that WAL
/// replay, MemTable scans, and B+Tree traversals all agree on sort order.
/// </summary>
internal static class ByteLexicographicalComparer
{
    /// <summary>
    /// Compares two spans byte-by-byte.  Shorter spans sort before longer ones when
    /// all leading bytes are equal (e.g. <c>[1,2]</c> &lt; <c>[1,2,3]</c>).
    /// </summary>
    public static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var minLength = Math.Min(left.Length, right.Length);
        for (var i = 0; i < minLength; i++)
        {
            var cmp = left[i].CompareTo(right[i]);
            if (cmp != 0)
                return cmp;
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>
    /// Compares two nullable byte arrays.  <c>null</c> is treated as an empty array.
    /// </summary>
    public static int Compare(byte[]? left, byte[]? right)
    {
        left ??= Array.Empty<byte>();
        right ??= Array.Empty<byte>();
        return Compare(left.AsSpan(), right.AsSpan());
    }
}
