// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;

namespace Walhalla.Storage.Core;

/// <summary>
/// Content-based equality comparer for <c>byte[]</c>.
/// Two arrays are considered equal when they have the same length and identical bytes,
/// regardless of object identity.  Uses FNV-1a 32-bit hashing for consistent dictionary
/// bucketing.
/// </summary>
internal sealed class ByteArrayContentComparer : IEqualityComparer<byte[]>
{
    /// <summary>Shared singleton instance.</summary>
    public static ByteArrayContentComparer Instance { get; } = new();

    /// <inheritdoc/>
    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x == null || y == null)
            return false;

        return x.AsSpan().SequenceEqual(y);
    }

    /// <inheritdoc/>
    public int GetHashCode(byte[] obj)
    {
        if (obj == null)
            return 0;

        unchecked
        {
            var hash = (int)2166136261;
            foreach (var b in obj)
                hash = (hash * 16777619) ^ b;
            return hash;
        }
    }
}

/// <summary>
/// Content-based equality comparer that treats <c>byte[]</c> keys by value and falls back
/// to the default object comparer for all other reference types.
/// Used by the MVCC transaction manager so that write/read lock tables key by row bytes,
/// not by array identity.
/// </summary>
internal sealed class ByteArrayObjectComparer : IEqualityComparer<object>
{
    public static ByteArrayObjectComparer Instance { get; } = new();

    public new bool Equals(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is byte[] bx && y is byte[] by)
            return bx.AsSpan().SequenceEqual(by);
        return EqualityComparer<object>.Default.Equals(x, y);
    }

    public int GetHashCode(object? obj)
    {
        if (obj is byte[] arr)
        {
            unchecked
            {
                var hash = (int)2166136261;
                foreach (var b in arr)
                    hash = (hash * 16777619) ^ b;
                return hash;
            }
        }
        return obj is null ? 0 : EqualityComparer<object>.Default.GetHashCode(obj);
    }
}
