// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;

namespace Walhalla.Storage.Ods.Tree;

/// <summary>
/// In-memory representation of a B+Tree leaf node loaded from the ODS file.
/// Leaf nodes hold the actual key-value (or key-tombstone) pairs and form a
/// doubly-linked list via <see cref="RightSiblingPageId"/> for efficient range scans.
/// </summary>
internal sealed class BPlusTreeLeafNode
{
    /// <summary>Page ID of this node in the ODS file.</summary>
    public int PageId { get; init; }

    /// <summary>Page ID of the right sibling leaf, or <c>-1</c> if this is the rightmost leaf.</summary>
    public int RightSiblingPageId { get; set; } = -1;

    /// <summary>Ordered list of keys stored in this leaf.  Parallel to <see cref="Values"/>.</summary>
    public List<byte[]> Keys { get; } = new();

    /// <summary>Ordered list of values stored in this leaf.  A <c>null</c> entry marks a delete tombstone.</summary>
    public List<byte[]> Values { get; } = new();
}
