// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Walhalla.Storage.Core.Configuration;

/// <summary>
/// Selects the on-disk data structure used to persist committed key-value data.
/// </summary>
public enum StorageMode
{
    /// <summary>
    /// Data is persisted in an on-disk B+Tree (ODS file).  This is the only production-ready
    /// storage mode in v1.0.  Provides O(log n) reads, O(log n) writes, and supports ordered
    /// range scans.
    /// </summary>
    BPlusTree = 0,

    /// <summary>Reserved for a future milestone.  Do not use.</summary>
    [Obsolete("LsmSstable is reserved for a future milestone and not yet implemented. Use BPlusTree.", error: true)]
    LsmSstable = 1
}
