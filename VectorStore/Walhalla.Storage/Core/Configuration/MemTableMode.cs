// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Core.Configuration;

/// <summary>
/// Determines where in-flight (not-yet-checkpointed) data is stored between WAL replay
/// and the next checkpoint.
/// </summary>
public enum MemTableMode
{
    /// <summary>
    /// All live data is kept in a <c>SortedDictionary</c> in the managed heap.
    /// Fastest reads and writes; memory grows until the next checkpoint.
    /// Suitable for workloads with bounded dataset sizes or frequent checkpoints.
    /// </summary>
    InMemory = 0,

    /// <summary>
    /// Live data is always written directly to an on-disk B+Tree (ODS file).
    /// Lowest memory footprint; recommended when dataset size exceeds available RAM.
    /// </summary>
    OnDiskBPlusTree = 1,

    /// <summary>
    /// Live data accumulates in memory up to <see cref="WalhallaOptions.HybridMemTableMaxBytes"/>,
    /// then spills to the on-disk delta B+Tree.  Balances read performance and memory usage.
    /// </summary>
    Hybrid = 2
}
