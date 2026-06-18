// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;
using Walhalla.Storage.Core.Comparers;

namespace Walhalla.Storage.Core;

/// <summary>
/// Adapts an <see cref="IKeyComparator"/> to the <see cref="IComparer{T}"/> interface
/// required by <see cref="System.Collections.Generic.SortedList{TKey,TValue}"/>.
/// </summary>
internal sealed class KeyComparatorAdapter : IComparer<byte[]>
{
    private readonly IKeyComparator _inner;

    public KeyComparatorAdapter(IKeyComparator inner)
    {
        _inner = inner;
    }

    public int Compare(byte[]? x, byte[]? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return  1;
        return _inner.Compare(x, y);
    }
}
