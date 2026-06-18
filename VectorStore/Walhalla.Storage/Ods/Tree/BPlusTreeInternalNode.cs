// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;

namespace Walhalla.Storage.Ods.Tree;

/// <summary>
/// In-memory representation of a B+Tree internal (routing) node loaded from the ODS file.
/// Internal nodes do not hold user data; they guide searches to the correct leaf by
/// comparing query keys against <see cref="SeparatorKeys"/>.
/// </summary>
internal sealed class BPlusTreeInternalNode
{
    /// <summary>Page ID of this node in the ODS file.</summary>
    public int PageId { get; init; }

    /// <summary>
    /// Separator keys that partition the key space.  For <c>n</c> separator keys there are
    /// always <c>n+1</c> child pointers in <see cref="ChildPageIds"/>.
    /// </summary>
    public List<byte[]> SeparatorKeys { get; } = new();

    /// <summary>Page IDs of the child nodes, ordered to match <see cref="SeparatorKeys"/>.</summary>
    public List<int> ChildPageIds { get; } = new();
}
