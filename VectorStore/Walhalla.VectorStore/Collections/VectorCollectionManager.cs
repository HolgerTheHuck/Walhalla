// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.VectorStore.Collections;

/// <summary>
/// Verwaltet mehrere Vektor-Collections in einem gemeinsamen Walhalla-Store.
/// </summary>
/// <remarks>
/// Key-Layout im Store:
/// <code>
/// c:{collection}:v:{id}  → Vektor-Bytes
/// c:{collection}:m:{id}  → Metadaten-JSON
/// c:{collection}:s       → Sequenznummer
/// c:{collection}:i       → Index-Metadata
/// </code>
/// </remarks>
public sealed class VectorCollectionManager : IDisposable
{
    private readonly IKeyValueStore _store;
    private readonly Dictionary<string, VectorCollection> _collections = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private bool _disposed;

    public VectorCollectionManager(IKeyValueStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Legacy-Konstruktor für Rückwärtskompatibilität. Erzeugt intern einen
    /// <see cref="BlobStoreIKeyValueAdapter"/>.
    /// </summary>
    public VectorCollectionManager(BlobStore store)
        : this(new BlobStoreIKeyValueAdapter(store))
    {
    }

    /// <summary>Erstellt oder öffnet eine Collection.</summary>
    public VectorCollection GetOrCreateCollection(string name, int dimension, DistanceMetric metric = DistanceMetric.Cosine, bool enableHnsw = true, HnswOptions? hnswOptions = null, bool enablePayloadIndex = true, bool enableIvf = false, IvfOptions? ivfOptions = null, PayloadIndexOptions? payloadIndexOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains(':')) throw new ArgumentException("Collection name cannot contain ':'", nameof(name));

        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_collections.TryGetValue(name, out var existing))
                return existing;

            _lock.EnterWriteLock();
            try
            {
                if (_collections.TryGetValue(name, out var doubleCheck))
                    return doubleCheck;

                var collection = new VectorCollection(name, dimension, metric, _store, enableHnsw, hnswOptions, enablePayloadIndex, enableIvf, ivfOptions, payloadIndexOptions);
                _collections[name] = collection;
                return collection;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    /// <summary>Holt eine existierende Collection.</summary>
    public VectorCollection? GetCollection(string name)
    {
        _lock.EnterReadLock();
        try
        {
            _collections.TryGetValue(name, out var collection);
            return collection;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Gibt alle Collections zurück.</summary>
    public IReadOnlyList<VectorCollection> GetCollections()
    {
        _lock.EnterReadLock();
        try
        {
            return _collections.Values.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Löscht eine Collection.</summary>
    public void DeleteCollection(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        _lock.EnterWriteLock();
        try
        {
            if (_collections.TryGetValue(name, out var collection))
            {
                collection.Dispose();
                _collections.Remove(name);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Erzeugt Point-in-Time Snapshot über alle Collections.</summary>
    public Snapshot CreateSnapshot()
    {
        _lock.EnterReadLock();
        try
        {
            var sequenceNumbers = new Dictionary<string, long>();
            foreach (var (name, coll) in _collections)
            {
                sequenceNumbers[name] = coll.CurrentSequence;
            }
            return new Snapshot(_store, sequenceNumbers, _collections.Keys.ToArray());
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _lock.EnterWriteLock();
        try
        {
            foreach (var collection in _collections.Values)
            {
                collection.Dispose();
            }
            _collections.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        _lock.Dispose();
        _disposed = true;
    }
}
