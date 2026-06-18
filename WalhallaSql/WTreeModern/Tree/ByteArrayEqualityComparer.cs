using System;
using System.Collections.Generic;

namespace WTreeModern.Tree;

internal sealed class ByteArrayEqualityComparer : IEqualityComparer<object>
{
    public new bool Equals(object? x, object? y)
    {
        if (x is byte[] bx && y is byte[] by)
            return bx.AsSpan().SequenceEqual(by);
        return EqualityComparer<object>.Default.Equals(x, y);
    }

    public int GetHashCode(object obj)
    {
        if (obj is byte[] bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
        return obj?.GetHashCode() ?? 0;
    }
}
