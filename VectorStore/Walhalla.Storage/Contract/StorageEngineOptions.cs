// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Core.Configuration;

namespace Walhalla.Storage.Contract;

/// <summary>
/// Konfiguration der Engine inkl. Overflow-Schwelle und Comparator.
/// </summary>
public sealed class StorageEngineOptions
{
    public required string RootPath { get; init; }

    /// <summary>Welcher Backend-Baum. Default = MvccBPlusTree (scan-optimiert).</summary>
    public StorageBackend Backend { get; init; } = StorageBackend.MvccBPlusTree;

    /// <summary>
    /// Werte ab dieser Größe gehen out-of-line (Overflow). 0 = nie inline-Limit.
    /// Default: 256 Bytes.
    /// </summary>
    public int OverflowThresholdBytes { get; init; } = 256;

    /// <summary>Byte-lexikografisch (Default) oder eigener Comparator.</summary>
    public IKeyComparator? KeyComparator { get; init; }

    /// <summary>WAL-Sync-Modus. Default: Fsync.</summary>
    public WalSyncMode WalSyncMode { get; init; } = WalSyncMode.Fsync;

    /// <summary>Größe des Value-Caches in Bytes. Default: 8 MiB.</summary>
    public long CacheSizeBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Millisekunden, die der Group-Commit-Flush nach dem ersten Pending-Commit wartet,
    /// um weitere concurrent commits zu koaleszieren. 0 = sofort flushen (default).
    /// </summary>
    public int GroupCommitCoalesceMs { get; init; } = 0;

    /// <summary>Page-Größe für ODS-Paging. Default: 4096 Bytes.</summary>
    public int PageSize { get; init; } = 4096;

    /// <summary>Anzahl gecacheter Pages (0 = kein Cache). Default: 0.</summary>
    public int PageCacheCapacity { get; init; } = 0;

    /// <summary>B+Tree-Order (max. Keys pro Knoten). Default: 128.</summary>
    public int Order { get; init; } = 128;
}
