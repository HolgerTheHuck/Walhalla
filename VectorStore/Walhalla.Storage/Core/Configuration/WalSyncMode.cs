// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Core.Configuration;

/// <summary>
/// Controls how the Write-Ahead Log is flushed to disk after each group-commit batch.
/// </summary>
public enum WalSyncMode
{
    /// <summary>
    /// After each batch, calls <c>FlushFileBuffers</c> (Windows) / <c>fsync</c> (Unix).
    /// Guarantees durability even after a power failure.
    /// Typical latency: ~0.1 ms (NVMe SSD) to ~15 ms (HDD / Windows timer resolution).
    /// This is the default.
    /// </summary>
    Fsync = 0,

    /// <summary>
    /// Opens the WAL file with <c>FILE_FLAG_WRITE_THROUGH</c> (Windows) / <c>O_DSYNC</c>-equivalent.
    /// Each write bypasses the OS page cache and lands directly at the disk controller.
    /// No additional <c>fsync</c> call is issued, which eliminates the ~15 ms
    /// <c>FlushFileBuffers</c> round-trip cost on Windows.
    /// Durability guarantee: survives OS crash; hardware power-loss safety depends on the
    /// disk's own write-back cache.
    /// Typical latency: sub-millisecond on NVMe SSDs.
    /// </summary>
    WriteThrough = 1,

    /// <summary>
    /// No sync at all — neither <c>WriteThrough</c> nor <c>fsync</c>.
    /// Data may be lost on OS crash.  Use only for ephemeral / test workloads.
    /// Highest throughput; OS page cache governs write timing.
    /// </summary>
    None = 2,
}
