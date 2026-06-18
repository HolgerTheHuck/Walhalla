// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Core.Configuration;

/// <summary>
/// Controls when individual key-value mutations are flushed from the WAL into the on-disk
/// B+Tree (ODS file).
/// </summary>
public enum OdsUpdateMode
{
    /// <summary>
    /// The ODS file is updated only during an explicit or auto-triggered checkpoint.
    /// This is the recommended setting: batch writes to the B+Tree minimise random I/O
    /// and amortise tree-rebalancing cost.
    /// </summary>
    CheckpointOnly = 0,

    /// <summary>
    /// Every committed transaction is written immediately to the ODS file.
    /// Useful when checkpoints are infrequent and read latency after a crash must be minimised,
    /// but incurs higher per-commit I/O overhead.
    /// </summary>
    Immediate = 1
}
