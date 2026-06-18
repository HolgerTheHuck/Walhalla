// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Walhalla.Storage.Core.Comparers;

/// <summary>
/// Defines a named, total-order comparison function for raw byte keys.
/// Implement this interface to provide a custom sort order for keys stored in a
/// <see cref="Walhalla.Storage.Core.Runtime.WalhallaStore"/>.
/// </summary>
/// <remarks>
/// A comparator must be deterministic, transitive, and anti-symmetric.  The same
/// <see cref="Id"/> must always produce the same ordering; changing the comparator
/// for an existing data file results in corruption.
/// </remarks>
public interface IKeyComparator
{
    /// <summary>
    /// Stable identifier for this comparator.  Used to look up the correct comparator
    /// when reopening an existing store.  Must be unique within the comparator registry
    /// passed via <see cref="Walhalla.Storage.Core.Configuration.WalhallaOptions.CustomKeyComparators"/>.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Compares two keys and returns a value indicating their relative order.
    /// </summary>
    /// <returns>
    /// A negative integer if <paramref name="left"/> precedes <paramref name="right"/>;
    /// zero if they are equal;
    /// a positive integer if <paramref name="left"/> follows <paramref name="right"/>.
    /// </returns>
    int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right);
}
