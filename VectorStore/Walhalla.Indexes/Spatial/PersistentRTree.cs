// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;
using Walhalla.Storage.Contract;

namespace Walhalla.Indexes.Spatial;

/// <summary>
/// Persistenter R-Tree auf Basis eines BlobStore-Snapshots.
/// </summary>
public sealed class PersistentRTree
{
    private readonly IKeyValueStore _store;
    private readonly byte[] _blobKey;
    private RTree _tree;

    public PersistentRTree(IKeyValueStore store, string keyPrefix, int dimensions = 2, int maxEntries = 16)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(keyPrefix);

        _blobKey = Encoding.UTF8.GetBytes(keyPrefix + "\0rtree");
        if (_store.TryGet(_blobKey, out var payload) && payload is not null)
        {
            _tree = RTree.Deserialize(payload);
        }
        else
        {
            _tree = new RTree(dimensions, maxEntries);
        }
    }

    public int EntryCount => _tree.EntryCount;

    public int NodeCount => _tree.NodeCount;

    public int Dimensions => _tree.Dimensions;

    public int MaxEntries => _tree.MaxEntries;

    public void Insert(long id, ReadOnlySpan<double> min, ReadOnlySpan<double> max)
    {
        _tree.Insert(id, min, max);
        Persist();
    }

    public bool Delete(long id)
    {
        bool removed = _tree.Delete(id);
        if (removed)
            Persist();

        return removed;
    }

    public IEnumerable<long> Search(ReadOnlySpan<double> min, ReadOnlySpan<double> max)
    {
        return _tree.Search(min, max);
    }

    public IReadOnlyList<RTreeEntry> ExportEntries()
    {
        return _tree.ExportEntries();
    }

    public void Clear()
    {
        _tree = new RTree(_tree.Dimensions, _tree.MaxEntries);
        _store.Delete(_blobKey);
    }

    private void Persist()
    {
        _store.Upsert(_blobKey, _tree.Serialize());
    }
}