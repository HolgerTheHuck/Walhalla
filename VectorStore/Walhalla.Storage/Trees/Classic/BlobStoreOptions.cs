// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using Walhalla.Storage.Core.Configuration;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Configuration options for a <see cref="BlobStore"/> instance.
/// Pass a fully configured instance to the <c>BlobStore</c> constructor; options are
/// frozen on entry and cannot be modified afterwards.
/// </summary>
public sealed class BlobStoreOptions
{
    /// <summary>
    /// Initialises options pointing at the given root directory.
    /// All data files (WAL, checkpoint, ODS, and the blob sidecar) are stored here.
    /// </summary>
    public BlobStoreOptions(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path must not be empty.", nameof(rootPath));
        RootPath = rootPath;
    }

    /// <summary>Root directory for all data files.</summary>
    public string RootPath { get; }

    /// <summary>
    /// File name of the append-only blob sidecar.  Default: <c>blobs.dat</c>.
    /// </summary>
    public string BlobFileName { get; set; } = "blobs.dat";

    /// <summary>
    /// Memory mode for the internal <see cref="Walhalla.Storage.Core.Runtime.WalhallaStore"/>
    /// that holds the blob pointers.
    /// Default: <see cref="MemTableMode.Hybrid"/> – balances RAM usage and pointer-lookup speed.
    /// </summary>
    public MemTableMode MemTableMode { get; set; } = MemTableMode.Hybrid;

    /// <summary>
    /// WAL size threshold (bytes) that triggers an automatic checkpoint of the pointer store.
    /// Default: 64 MiB.  Set to <c>0</c> to disable auto-checkpoint.
    /// </summary>
    public long AutoCheckpointWalThresholdBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Controls how the Write-Ahead Log and blob sidecar are flushed to disk.
    /// <see cref="Walhalla.Storage.Core.Configuration.WalSyncMode.Fsync"/> is the default
    /// (safest, but slowest). Use <see cref="Walhalla.Storage.Core.Configuration.WalSyncMode.WriteThrough"/>
    /// for better throughput on NVMe SSDs, or <see cref="Walhalla.Storage.Core.Configuration.WalSyncMode.None"/>
    /// for maximum ingest performance (data may be lost on OS crash).
    /// </summary>
    public Walhalla.Storage.Core.Configuration.WalSyncMode WalSyncMode { get; set; }
        = Walhalla.Storage.Core.Configuration.WalSyncMode.Fsync;

    // ── internal helpers ───────────────────────────────────────────────────────

    internal string BlobFilePath => Path.Combine(RootPath, BlobFileName);

    /// <summary>Temporary file written during <see cref="BlobStore.CompactAsync"/>.</summary>
    internal string BlobTmpFilePath => BlobFilePath + ".tmp";

    internal WalhallaOptions BuildWalhallaOptions() =>
        new(RootPath)
        {
            MemTableMode          = MemTableMode,
            AutoCheckpointWalThresholdBytes = AutoCheckpointWalThresholdBytes,
            WalSyncMode           = WalSyncMode,
        };
}
