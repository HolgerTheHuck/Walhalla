// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace WTreeModern;

/// <summary>
/// Controls how the WAL and data file are flushed to disk during commit.
/// </summary>
public enum WTreeSyncMode
{
    /// <summary>
    /// WAL uses <c>FileOptions.WriteThrough</c> + <c>Flush(true)</c>;
    /// data file uses <c>Flush(true)</c>.
    /// Guarantees durability even after a power failure.
    /// </summary>
    Full = 0,

    /// <summary>
    /// WAL uses <c>FileOptions.WriteThrough</c> but no explicit <c>Flush(true)</c>;
    /// data file still uses <c>Flush(true)</c>.
    /// Survives OS crash; hardware power-loss safety depends on the disk's write-back cache.
    /// </summary>
    WriteThrough = 1,

    /// <summary>
    /// No sync anywhere. No <c>WriteThrough</c>, no <c>Flush(true)</c>.
    /// Data may be lost on OS crash. Use only for ephemeral / test workloads.
    /// </summary>
    None = 2,
}
