// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class SnapshotTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BlobStore _store;
    private readonly VectorCollectionManager _manager;

    public SnapshotTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_snap_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbPath);
        _store = new BlobStore(new BlobStoreOptions(_dbPath));
        _manager = new VectorCollectionManager(_store);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        _store?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }

    [Fact]
    public async Task Snapshot_Iterator_ReturnsAllData()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        await collection.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));
        await collection.PutAsync(2, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));

        var snapshot = _manager.CreateSnapshot();
        var iterator = snapshot.CreateIterator("test", 3);

        // Add more data after snapshot
        await collection.PutAsync(3, new Vector(new float[] { 0.0f, 0.0f, 1.0f }));

        // Iterator scans current store state, so it sees all 3 items
        var records = await iterator.ToListAsync();
        var ids = records.Select(r => r.Id).ToList();

        Assert.Equal(3, ids.Count);
        Assert.Contains(1ul, ids);
        Assert.Contains(2ul, ids);
        Assert.Contains(3ul, ids);
    }

    [Fact]
    public void Snapshot_CreateIterator_NonExisting_ThrowsArgumentException()
    {
        var snapshot = _manager.CreateSnapshot();

        Assert.Throws<ArgumentException>(() => snapshot.CreateIterator("nonexistent", 3));
    }

    [Fact]
    public void Snapshot_CollectionNames_ListsAllCollections()
    {
        _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);
        _manager.GetOrCreateCollection("images", 256, DistanceMetric.Euclidean);

        var snapshot = _manager.CreateSnapshot();

        Assert.Equal(2, snapshot.CollectionNames.Count);
        Assert.Contains("docs", snapshot.CollectionNames);
        Assert.Contains("images", snapshot.CollectionNames);
    }

    [Fact]
    public async Task Snapshot_CreateIterator_ReturnsCorrectData()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        await collection.PutAsync(1, vector);

        var snapshot = _manager.CreateSnapshot();
        var iterator = snapshot.CreateIterator("test", 3);
        var records = await iterator.ToListAsync();

        Assert.Single(records);
        Assert.Equal(1ul, records[0].Id);
        Assert.True(vector.Span.SequenceEqual(records[0].Vector.Span));
    }

    [Fact]
    public async Task Snapshot_CreateIterator_ReturnsCurrentStoreState()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        await collection.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));
        await collection.PutAsync(2, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));

        var snapshot = _manager.CreateSnapshot();
        var iterator = snapshot.CreateIterator("test", 3);

        // Delete after snapshot - store no longer has the item
        await collection.DeleteAsync(1);

        var records = await iterator.ToListAsync();
        var ids = records.Select(r => r.Id).ToList();

        Assert.Single(ids);
        Assert.Contains(2ul, ids);
    }

    [Fact]
    public void Snapshot_Dispose_DoesNotThrow()
    {
        var snapshot = _manager.CreateSnapshot();

        snapshot.Dispose();

        // Should not throw
    }

    [Fact]
    public async Task Snapshot_MultipleSnapshots_SeeSameData()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        await collection.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));

        var snap1 = _manager.CreateSnapshot();

        await collection.PutAsync(2, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));

        var snap2 = _manager.CreateSnapshot();

        // Both iterators scan current store state, so both see all items
        var ids1 = (await snap1.CreateIterator("test", 3).ToListAsync()).Select(r => r.Id).ToList();
        var ids2 = (await snap2.CreateIterator("test", 3).ToListAsync()).Select(r => r.Id).ToList();

        Assert.Equal(2, ids1.Count);
        Assert.Equal(2, ids2.Count);
    }
}
