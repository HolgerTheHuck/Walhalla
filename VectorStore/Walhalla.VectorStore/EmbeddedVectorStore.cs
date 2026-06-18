// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.VectorStore;

/// <summary>
/// Ein embedded Vector-Store für .NET – zero-config, datei-basiert, standalone.
/// </summary>
/// <remarks>
/// <para>SQLite für Vektoren: Ein einzelnes Verzeichnis, keine Server, keine Dependencies.</para>
/// <para>Ideal für lokale Agenten, Desktop-Apps, Tools und Games.</para>
/// </remarks>
/// <example>
/// <code>
/// using var store = new EmbeddedVectorStore("my_data");
/// var docs = store.GetOrCreateCollection("documents", 1536);
/// await docs.UpsertAsync(1, new Vector(...), new() { ["title"] = "Hello" });
/// var results = await docs.SearchAsync(new Vector(...), topK: 5);
/// </code>
/// </example>
public sealed class EmbeddedVectorStore : IDisposable
{
    private readonly IKeyValueStore _store;
    private readonly VectorCollectionManager _manager;
    private readonly ReaderWriterLockSlim _lock = new();
    private bool _disposed;

    private readonly string _path;

    /// <summary>
    /// Öffnet oder erstellt einen Vector-Store im angegebenen Verzeichnis.
    /// Default-Backend: <see cref="StorageBackend.MvccBPlusTree"/>.
    /// </summary>
    public EmbeddedVectorStore(string path)
        : this(new StorageEngineOptions { RootPath = path })
    {
    }

    /// <summary>
    /// Öffnet oder erstellt einen Vector-Store mit konfigurierbaren Blob-Storage-Optionen.
    /// Expliziter Einstieg für das klassische BPlusTree-/BlobStore-Backend.
    /// </summary>
    public EmbeddedVectorStore(BlobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.RootPath);
        _path = options.RootPath;

        var blobStore = new BlobStore(options);
        _store = new BlobStoreIKeyValueAdapter(blobStore);
        _manager = new VectorCollectionManager(_store);
    }

    /// <summary>
    /// Öffnet oder erstellt einen Vector-Store mit wählbarem Storage-Backend.
    /// <para>Default: <see cref="StorageBackend.MvccBPlusTree"/>.
    /// Für den Legacy-BPlusTree-Pfad verwenden Sie <see cref="BlobStoreOptions"/>.</para>
    /// </summary>
    public EmbeddedVectorStore(StorageEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.RootPath);
        _path = options.RootPath;

        _store = options.Backend switch
        {
            StorageBackend.MvccBPlusTree => new MvccBPlusTreeStore(
                odsPath: Path.Combine(options.RootPath, "data.ods"),
                pageSize: options.PageSize,
                pageCacheCapacity: options.PageCacheCapacity,
                keyComparator: options.KeyComparator,
                order: options.Order,
                walPath: Path.Combine(options.RootPath, "wal.dat"),
                walSyncMode: options.WalSyncMode,
                overflowThreshold: options.OverflowThresholdBytes),

            StorageBackend.BPlusTree => new BlobStoreIKeyValueAdapter(
                new BlobStore(new BlobStoreOptions(options.RootPath))),

            _ => throw new NotSupportedException(
                $"Backend '{options.Backend}' wird für EmbeddedVectorStore noch nicht unterstützt. " +
                "Unterstützt: MvccBPlusTree, BPlusTree (Legacy).")
        };

        _manager = new VectorCollectionManager(_store);
    }

    /// <summary>Erstellt oder öffnet eine Collection.</summary>
    public VectorCollection GetOrCreateCollection(
        string name,
        int dimension,
        DistanceMetric metric = DistanceMetric.Cosine,
        bool enableHnsw = true,
        HnswOptions? hnswOptions = null,
        bool enableIvf = false,
        IvfOptions? ivfOptions = null,
        PayloadIndexOptions? payloadIndexOptions = null)
    {
        _lock.EnterReadLock();
        try
        {
            return _manager.GetOrCreateCollection(name, dimension, metric, enableHnsw, hnswOptions, enablePayloadIndex: true, enableIvf: enableIvf, ivfOptions: ivfOptions, payloadIndexOptions: payloadIndexOptions);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Holt eine existierende Collection oder null.</summary>
    public VectorCollection? GetCollection(string name)
    {
        _lock.EnterReadLock();
        try
        {
            return _manager.GetCollection(name);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Liste aller Collections.</summary>
    public IReadOnlyList<VectorCollection> GetCollections()
    {
        _lock.EnterReadLock();
        try
        {
            return _manager.GetCollections();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Löscht eine Collection.</summary>
    public void DeleteCollection(string name)
    {
        _lock.EnterWriteLock();
        try
        {
            _manager.DeleteCollection(name);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Erzeugt einen konsistenten Snapshot über alle Collections.</summary>
    public Snapshot CreateSnapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return _manager.CreateSnapshot();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Schreibt alle pending Changes auf Disk.</summary>
    public Task CheckpointAsync(CancellationToken ct = default)
        => _store.CheckpointAsync(ct);

    /// <summary>Gesamtgröße des Stores auf Disk in Bytes.</summary>
    public long GetDiskSize()
    {
        var dir = new DirectoryInfo(_path);
        return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _lock.Dispose();
        _manager.Dispose();
        _store.Dispose();
        _disposed = true;
    }
}
