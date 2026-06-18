// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Walhalla.Storage.Core.Caching;

namespace Walhalla.Storage.Core;

/// <summary>
/// A point-in-time snapshot of operational metrics for a <see cref="Runtime.WalhallaStore"/> instance.
/// Obtain via <see cref="Runtime.WalhallaStore.GetDiagnostics"/>.
/// </summary>
public readonly record struct WalhallaDiagnostics(
    /// <summary>Current size of the WAL file on disk, in bytes.</summary>
    long WalFileSizeBytes,

    /// <summary>Number of live entries in the in-memory memtable.</summary>
    long MemTableEntries,

    /// <summary>Approximate size of the in-memory memtable in bytes (key + value lengths).</summary>
    long MemTableApproxBytes,

    /// <summary>
    /// Number of entries in the on-disk delta B+Tree (includes delete-markers).
    /// Always <c>0</c> when <see cref="Configuration.MemTableMode"/> is <see cref="Configuration.MemTableMode.InMemory"/>.
    /// </summary>
    long DeltaEntryCount,

    /// <summary>Total number of checkpoints completed since this instance was created (includes auto-checkpoints).</summary>
    long TotalCheckpoints,

    /// <summary>
    /// Total number of MemTable-to-Delta spills since this instance was created.
    /// Relevant only for <see cref="Configuration.MemTableMode.Hybrid"/> mode.
    /// </summary>
    long TotalSpills,

    /// <summary>Wall-clock duration of the most recently completed checkpoint. <see cref="TimeSpan.Zero"/> before the first checkpoint.</summary>
    TimeSpan LastCheckpointDuration,

    /// <summary>Current LRU value-cache statistics.</summary>
    CacheStatistics Cache,

    /// <summary>
    /// Number of group-commit flush operations since this instance was created.
    /// Each flush persists one or more transactions with a single fsync.
    /// Divide <see cref="TotalGroupedTransactions"/> by this value to get the average batch size.
    /// </summary>
    long TotalGroupCommitFlushes,

    /// <summary>
    /// Total number of transactions committed via the group-commit flush loop since this instance was created.
    /// </summary>
    long TotalGroupedTransactions);
