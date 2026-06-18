// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Ods.Pages;

/// <summary>Identifies the role of a fixed-size ODS page on disk.</summary>
internal enum OdsPageType : byte
{
    /// <summary>Uninitialized or unrecognised page.  Should not appear in a valid ODS file.</summary>
    Unknown = 0,

    /// <summary>Page 0 of every ODS file: stores the B+Tree root pointer and last-allocated page ID.</summary>
    Meta = 1,

    /// <summary>An internal (routing) node of the B+Tree.  Contains separator keys and child page IDs.</summary>
    Internal = 2,

    /// <summary>A leaf node of the B+Tree.  Stores the actual key-value (or key-tombstone) pairs.</summary>
    Leaf = 3
}
