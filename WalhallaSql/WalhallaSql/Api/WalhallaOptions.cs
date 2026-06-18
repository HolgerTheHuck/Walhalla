using System;
using System.IO;
using WalhallaSql.Core;

namespace WalhallaSql;

public sealed class WalhallaOptions
{
    private bool _frozen;
    private string _walFileName = "wal.log";
    private string _checkpointFileName = "checkpoint.bin";
    private string _odsFileName = "ods.dat";
    private string _deltaFileName = "delta.ods";
    private int _odsPageSizeBytes = 4096;
    private long _cacheSizeBytes = 8 * 1024 * 1024;
    private StorageMode _storageMode = StorageMode.MvccBPlusTree;
    private OdsUpdateMode _odsUpdateMode = OdsUpdateMode.CheckpointOnly;
    private MemTableMode _memTableMode = MemTableMode.InMemory;
    private long _hybridMemTableMaxBytes = 64L * 1024 * 1024;
    private long _autoCheckpointWalThresholdBytes = 64L * 1024 * 1024;
    private int _pageCacheCapacity = 512;
    private WalSyncMode _walSyncMode = WalSyncMode.Fsync;
    private int _groupCommitCoalesceMs = 0;
    private TransactionMode? _transactionMode;
    private int _maxTransactionRetries = 5;
    private int _recursiveCteMaxIterations = 1000;
    private int _planCacheCapacity = 10_000;
    private int _blobInliningThreshold = 2048;
    private bool _enableBlobSidecar = true;
    private string _blobSidecarRootPath = "blobs";

    public WalhallaOptions(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path must not be empty.", nameof(rootPath));
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public string WalFileName
    {
        get => _walFileName;
        set { ThrowIfFrozen(); _walFileName = value; }
    }

    public string CheckpointFileName
    {
        get => _checkpointFileName;
        set { ThrowIfFrozen(); _checkpointFileName = value; }
    }

    public string OdsFileName
    {
        get => _odsFileName;
        set { ThrowIfFrozen(); _odsFileName = value; }
    }

    public string DeltaFileName
    {
        get => _deltaFileName;
        set { ThrowIfFrozen(); _deltaFileName = value; }
    }

    public int OdsPageSizeBytes
    {
        get => _odsPageSizeBytes;
        set { ThrowIfFrozen(); _odsPageSizeBytes = value; }
    }

    public long CacheSizeBytes
    {
        get => _cacheSizeBytes;
        set { ThrowIfFrozen(); _cacheSizeBytes = value; }
    }

    public StorageMode StorageMode
    {
        get => _storageMode;
        set { ThrowIfFrozen(); _storageMode = value; }
    }

    public TransactionMode? TransactionMode
    {
        get => _transactionMode;
        set { ThrowIfFrozen(); _transactionMode = value; }
    }

    public OdsUpdateMode OdsUpdateMode
    {
        get => _odsUpdateMode;
        set { ThrowIfFrozen(); _odsUpdateMode = value; }
    }

    public MemTableMode MemTableMode
    {
        get => _memTableMode;
        set { ThrowIfFrozen(); _memTableMode = value; }
    }

    public long HybridMemTableMaxBytes
    {
        get => _hybridMemTableMaxBytes;
        set { ThrowIfFrozen(); _hybridMemTableMaxBytes = value; }
    }

    public long AutoCheckpointWalThresholdBytes
    {
        get => _autoCheckpointWalThresholdBytes;
        set
        {
            ThrowIfFrozen();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "AutoCheckpointWalThresholdBytes must be >= 0.");
            _autoCheckpointWalThresholdBytes = value;
        }
    }

    public int PageCacheCapacity
    {
        get => _pageCacheCapacity;
        set
        {
            ThrowIfFrozen();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Page cache capacity must be >= 0.");
            _pageCacheCapacity = value;
        }
    }

    public WalSyncMode WalSyncMode
    {
        get => _walSyncMode;
        set { ThrowIfFrozen(); _walSyncMode = value; }
    }

    public int GroupCommitCoalesceMs
    {
        get => _groupCommitCoalesceMs;
        set
        {
            ThrowIfFrozen();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "GroupCommitCoalesceMs must be >= 0.");
            _groupCommitCoalesceMs = value;
        }
    }

    public int MaxTransactionRetries
    {
        get => _maxTransactionRetries;
        set
        {
            ThrowIfFrozen();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxTransactionRetries must be >= 0.");
            _maxTransactionRetries = value;
        }
    }

    public int RecursiveCteMaxIterations
    {
        get => _recursiveCteMaxIterations;
        set
        {
            ThrowIfFrozen();
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value), "RecursiveCteMaxIterations must be >= 1.");
            _recursiveCteMaxIterations = value;
        }
    }

    public int PlanCacheCapacity
    {
        get => _planCacheCapacity;
        set
        {
            ThrowIfFrozen();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "PlanCacheCapacity must be >= 0. Use 0 to disable.");
            _planCacheCapacity = value;
        }
    }

    public int BlobInliningThreshold
    {
        get => _blobInliningThreshold;
        set
        {
            ThrowIfFrozen();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "BlobInliningThreshold must be >= 0. Use 0 to always offload.");
            _blobInliningThreshold = value;
        }
    }

    public bool EnableBlobSidecar
    {
        get => _enableBlobSidecar;
        set { ThrowIfFrozen(); _enableBlobSidecar = value; }
    }

    public string BlobSidecarRootPath
    {
        get => _blobSidecarRootPath;
        set { ThrowIfFrozen(); _blobSidecarRootPath = value; }
    }

    internal string WalFilePath => Path.Combine(RootPath, WalFileName);
    internal string CheckpointFilePath => Path.Combine(RootPath, CheckpointFileName);
    internal string OdsFilePath => Path.Combine(RootPath, OdsFileName);
    internal string DeltaFilePath => Path.Combine(RootPath, DeltaFileName);
    internal string BlobSidecarRootDirectory => Path.IsPathRooted(_blobSidecarRootPath)
        ? _blobSidecarRootPath
        : Path.Combine(RootPath, _blobSidecarRootPath);
    internal string DeltaTmpFilePath => Path.Combine(RootPath, DeltaFileName + ".tmp");

    internal void Freeze() => _frozen = true;

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                "WalhallaOptions cannot be modified after being passed to WalhallaEngine.");
    }
}
