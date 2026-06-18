// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Core.Transformers;

namespace Walhalla.Storage.Core.Configuration;

/// <summary>
/// Mutable configuration object for a <see cref="Walhalla.Storage.Core.Runtime.WalhallaStore"/> instance.
/// Pass a fully configured instance to the <c>WalhallaStore</c> constructor; the options are
/// frozen on entry and cannot be modified afterwards.
/// </summary>
/// <remarks>
/// All file-name properties are relative; the absolute path is derived by combining them with
/// <see cref="RootPath"/>.  To store data in memory only set <see cref="MemTableMode"/> to
/// <see cref="MemTableMode.InMemory"/> and <see cref="AutoCheckpointWalThresholdBytes"/> to <c>0</c>.
/// </remarks>
public sealed class WalhallaOptions
{
    private bool _frozen;
    private string _walFileName = "wal.log";
    private string _checkpointFileName = "checkpoint.bin";
    private string _odsFileName = "ods.dat";
    private string _deltaFileName = "delta.ods";
    private string _odsMetadataFileName = "ods.meta.json";
    private int _odsPageSizeBytes = 4096;
    private long _cacheSizeBytes = 8 * 1024 * 1024;
    private StorageMode _storageMode = StorageMode.BPlusTree;
    private OdsUpdateMode _odsUpdateMode = OdsUpdateMode.CheckpointOnly;
    private MemTableMode _memTableMode = MemTableMode.InMemory;
    private long _hybridMemTableMaxBytes = 64L * 1024 * 1024;
    private long _autoCheckpointWalThresholdBytes = 64L * 1024 * 1024;
    private string _keyComparatorId = BuiltInKeyComparators.BytewiseId;
    private IReadOnlyDictionary<string, IKeyComparator>? _customKeyComparators;
    private int _pageCacheCapacity = 512;
    private IValueTransformer? _transformer;

    public WalhallaOptions(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path must not be empty.", nameof(rootPath));

        RootPath = rootPath;
    }

    /// <summary>Root directory where all data files are stored.  Set once at construction; cannot be changed.</summary>
    public string RootPath { get; }

    /// <summary>File name of the Write-Ahead Log.  Default: <c>wal.log</c>.</summary>
    public string WalFileName
    {
        get => _walFileName;
        set { ThrowIfFrozen(); _walFileName = value; }
    }

    /// <summary>File name of the checkpoint snapshot.  Default: <c>checkpoint.bin</c>.</summary>
    public string CheckpointFileName
    {
        get => _checkpointFileName;
        set { ThrowIfFrozen(); _checkpointFileName = value; }
    }

    /// <summary>File name of the primary on-disk B+Tree (ODS).  Default: <c>ods.dat</c>.</summary>
    public string OdsFileName
    {
        get => _odsFileName;
        set { ThrowIfFrozen(); _odsFileName = value; }
    }

    /// <summary>File name of the delta B+Tree used in <see cref="MemTableMode.Hybrid"/> mode.  Default: <c>delta.ods</c>.</summary>
    public string DeltaFileName
    {
        get => _deltaFileName;
        set { ThrowIfFrozen(); _deltaFileName = value; }
    }

    /// <summary>File name of the JSON metadata file that accompanies each ODS file.  Default: <c>ods.meta.json</c>.</summary>
    public string OdsMetadataFileName
    {
        get => _odsMetadataFileName;
        set { ThrowIfFrozen(); _odsMetadataFileName = value; }
    }

    /// <summary>Fixed page size used by the ODS pager, in bytes.  Must be a multiple of 512.  Default: <c>4096</c>.</summary>
    public int OdsPageSizeBytes
    {
        get => _odsPageSizeBytes;
        set { ThrowIfFrozen(); _odsPageSizeBytes = value; }
    }

    /// <summary>Maximum size of the in-process value cache, in bytes.  Default: <c>8 MiB</c>.</summary>
    public long CacheSizeBytes
    {
        get => _cacheSizeBytes;
        set { ThrowIfFrozen(); _cacheSizeBytes = value; }
    }

    /// <summary>Selects the on-disk data structure.  Only <see cref="Configuration.StorageMode.BPlusTree"/> is supported in v1.0.  Default: <see cref="Configuration.StorageMode.BPlusTree"/>.</summary>
    public StorageMode StorageMode
    {
        get => _storageMode;
        set { ThrowIfFrozen(); _storageMode = value; }
    }

    /// <summary>Controls when mutations are written to the ODS file.  Default: <see cref="Configuration.OdsUpdateMode.CheckpointOnly"/>.</summary>
    public OdsUpdateMode OdsUpdateMode
    {
        get => _odsUpdateMode;
        set { ThrowIfFrozen(); _odsUpdateMode = value; }
    }

    /// <summary>Determines where live (not-yet-checkpointed) data is held between checkpoints.  Default: <see cref="Configuration.MemTableMode.InMemory"/>.</summary>
    public MemTableMode MemTableMode
    {
        get => _memTableMode;
        set { ThrowIfFrozen(); _memTableMode = value; }
    }

    /// <summary>
    /// Maximum in-memory MemTable size before a spill to the delta B+Tree occurs.
    /// Only relevant for <see cref="Configuration.MemTableMode.Hybrid"/> mode.  Default: <c>64 MiB</c>.
    /// </summary>
    public long HybridMemTableMaxBytes
    {
        get => _hybridMemTableMaxBytes;
        set { ThrowIfFrozen(); _hybridMemTableMaxBytes = value; }
    }

    /// <summary>
    /// Minimum WAL file size (in bytes) that triggers an automatic checkpoint after each commit.
    /// Set to <c>0</c> to disable auto-checkpoint. Default: <c>64 MiB</c>.
    /// </summary>
    public long AutoCheckpointWalThresholdBytes
    {
        get => _autoCheckpointWalThresholdBytes;
        set
        {
            ThrowIfFrozen();
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "AutoCheckpointWalThresholdBytes must be >= 0.");
            _autoCheckpointWalThresholdBytes = value;
        }
    }

    /// <summary>
    /// ID of the key comparator to use.  Must match a key in <see cref="Comparers.BuiltInKeyComparators.All"/>
    /// or in <see cref="CustomKeyComparators"/>.  Default: <see cref="Comparers.BuiltInKeyComparators.BytewiseId"/>.
    /// </summary>
    public string KeyComparatorId
    {
        get => _keyComparatorId;
        set { ThrowIfFrozen(); _keyComparatorId = value; }
    }

    /// <summary>
    /// Optional set of application-defined key comparators that can be referenced by
    /// <see cref="KeyComparatorId"/>.  Built-in comparators are always available and do not
    /// need to be repeated here.
    /// </summary>
    public IReadOnlyDictionary<string, IKeyComparator>? CustomKeyComparators
    {
        get => _customKeyComparators;
        set { ThrowIfFrozen(); _customKeyComparators = value; }
    }

    /// <summary>
    /// Optional transformer (compress / encrypt) applied to every value before it is written to
    /// disk and reversed when values are read back.  The transformer does <b>not</b> affect keys.
    /// </summary>
    /// <remarks>
    /// Use <see cref="Walhalla.Storage.Core.Transformers.ValueTransformerExtensions.Then"/> to
    /// chain multiple transformers in the correct order:
    /// <code>
    /// // Compress before encrypting – always the right order
    /// options.Transformer = new LZ4Transformer().Then(new AesGcmTransformer(key));
    /// </code>
    /// The transformer is applied at the <c>Put</c>/<c>TryGet</c>/<c>Scan</c> API boundary.
    /// Raw transactions obtained via <c>BeginTransaction()</c> operate on unencoded bytes and
    /// bypass this setting.
    /// </remarks>
    public IValueTransformer? Transformer
    {
        get => _transformer;
        set { ThrowIfFrozen(); _transformer = value; }
    }

    /// <summary>
    /// Maximum number of ODS pages kept in the in-process LRU page cache per tree (ODS + Delta).
    /// A higher value reduces disk reads for repeated B+Tree traversals at the cost of additional RAM.
    /// Set to <c>0</c> to disable the cache.  Default: <c>512</c> pages.
    /// </summary>
    public int PageCacheCapacity
    {
        get => _pageCacheCapacity;
        set
        {
            ThrowIfFrozen();
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Page cache capacity must be >= 0.");
            _pageCacheCapacity = value;
        }
    }

    private WalSyncMode _walSyncMode = WalSyncMode.Fsync;

    /// <summary>
    /// Controls how the Write-Ahead Log is flushed to disk after each group-commit batch.
    /// <list type="bullet">
    ///   <item><see cref="WalSyncMode.Fsync"/> — full fsync after every batch (default, safest).</item>
    ///   <item><see cref="WalSyncMode.WriteThrough"/> — FILE_FLAG_WRITE_THROUGH; no fsync call;
    ///     eliminates the ~15 ms FlushFileBuffers round-trip on Windows while still surviving OS crash.</item>
    ///   <item><see cref="WalSyncMode.None"/> — no sync; highest throughput, ephemeral/test only.</item>
    /// </list>
    /// Default: <see cref="WalSyncMode.Fsync"/>.
    /// </summary>
    public WalSyncMode WalSyncMode
    {
        get => _walSyncMode;
        set { ThrowIfFrozen(); _walSyncMode = value; }
    }

    private int _groupCommitCoalesceMs = 0;

    /// <summary>
    /// Milliseconds to wait after the first pending commit arrives before flushing the
    /// group-commit batch to disk.  During this window additional concurrent commits can
    /// join the batch, resulting in fewer fsyncs under parallel write workloads.
    /// <para>
    /// Set to <c>0</c> (the default) to flush immediately — lowest single-writer latency.
    /// Set to <c>1</c>–<c>5</c> for high-concurrency workloads where throughput matters more
    /// than absolute write latency.  Note that on Windows the actual OS timer resolution is
    /// typically ~15 ms, so even a value of <c>1</c> may coalesce a full 15 ms window.
    /// </para>
    /// Default: <c>0</c> (disabled — immediate flush).
    /// </summary>
    public int GroupCommitCoalesceMs
    {
        get => _groupCommitCoalesceMs;
        set
        {
            ThrowIfFrozen();
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "GroupCommitCoalesceMs must be >= 0.");
            _groupCommitCoalesceMs = value;
        }
    }

    internal string WalFilePath => Path.Combine(RootPath, WalFileName);

    internal string CheckpointFilePath => Path.Combine(RootPath, CheckpointFileName);

    internal string OdsFilePath => Path.Combine(RootPath, OdsFileName);

    internal string DeltaFilePath => Path.Combine(RootPath, DeltaFileName);

    internal string OdsMetadataFilePath => Path.Combine(RootPath, OdsMetadataFileName);

    /// <summary>Temporary path used during compaction. Written first; renamed to <see cref="OdsFilePath"/> atomically.</summary>
    internal string OdsTmpFilePath => Path.Combine(RootPath, OdsFileName + ".tmp");

    /// <summary>Temporary path used when resetting the delta tree during compaction.</summary>
    internal string DeltaTmpFilePath => Path.Combine(RootPath, DeltaFileName + ".tmp");

    /// <summary>
    /// Prevents further modifications to these options. Called by <see cref="WalhallaStore"/> on construction.
    /// </summary>
    internal void Freeze() => _frozen = true;

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                "WalhallaOptions cannot be modified after being passed to WalhallaStore.");
    }
}
