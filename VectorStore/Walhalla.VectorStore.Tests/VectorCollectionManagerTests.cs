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

public class VectorCollectionManagerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BlobStore _store;
    private readonly VectorCollectionManager _manager;

    public VectorCollectionManagerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_mgr_test_{Guid.NewGuid()}");
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
    public void GetOrCreateCollection_NewCollection_ReturnsCollection()
    {
        var collection = _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);

        Assert.NotNull(collection);
        Assert.Equal("docs", collection.Name);
        Assert.Equal(128, collection.Dimension);
    }

    [Fact]
    public void GetOrCreateCollection_ExistingCollection_ReturnsSameInstance()
    {
        var c1 = _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);
        var c2 = _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);

        Assert.Same(c1, c2);
    }

    [Fact]
    public void GetOrCreateCollection_DifferentNames_DifferentInstances()
    {
        var c1 = _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);
        var c2 = _manager.GetOrCreateCollection("images", 128, DistanceMetric.Cosine);

        Assert.NotSame(c1, c2);
    }

    [Fact]
    public void GetOrCreateCollection_DifferentDimensions_ReturnsExisting()
    {
        var existing = _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);

        var result = _manager.GetOrCreateCollection("docs", 256, DistanceMetric.Cosine);

        Assert.Same(existing, result);
    }

    [Fact]
    public void GetOrCreateCollection_DifferentMetrics_ReturnsExisting()
    {
        var existing = _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);

        var result = _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Euclidean);

        Assert.Same(existing, result);
    }

    [Fact]
    public void GetCollection_Existing_ReturnsCollection()
    {
        var created = _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);
        var retrieved = _manager.GetCollection("docs");

        Assert.Same(created, retrieved);
    }

    [Fact]
    public void GetCollection_NonExisting_ReturnsNull()
    {
        var collection = _manager.GetCollection("nonexistent");

        Assert.Null(collection);
    }

    [Fact]
    public void CollectionNames_ListsAllCollections()
    {
        _manager.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);
        _manager.GetOrCreateCollection("images", 256, DistanceMetric.Euclidean);
        _manager.GetOrCreateCollection("audio", 64, DistanceMetric.DotProduct);

        // Use reflection or check via GetCollection
        Assert.NotNull(_manager.GetCollection("docs"));
        Assert.NotNull(_manager.GetCollection("images"));
        Assert.NotNull(_manager.GetCollection("audio"));
    }

    [Fact]
    public void CollectionNames_EmptyManager_ReturnsEmpty()
    {
        Assert.Null(_manager.GetCollection("any"));
    }

    [Fact]
    public async Task MultipleCollections_IsolatedData()
    {
        var docs = _manager.GetOrCreateCollection("docs", 3, DistanceMetric.Euclidean);
        var images = _manager.GetOrCreateCollection("images", 3, DistanceMetric.Euclidean);

        await docs.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));
        await images.PutAsync(1, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));

        var docEntry = await docs.GetAsync(1);
        var imageEntry = await images.GetAsync(1);

        Assert.NotNull(docEntry);
        Assert.NotNull(imageEntry);
        Assert.Equal(1.0f, docEntry.Vector.Span[0]);
        Assert.Equal(1.0f, imageEntry.Vector.Span[1]);
    }

    [Fact]
    public async Task CreateSnapshot_CapturesState()
    {
        var docs = _manager.GetOrCreateCollection("docs", 3, DistanceMetric.Euclidean);
        var images = _manager.GetOrCreateCollection("images", 3, DistanceMetric.Euclidean);

        await docs.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));
        await images.PutAsync(1, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));

        var snapshot = _manager.CreateSnapshot();

        Assert.NotNull(snapshot);
        Assert.Contains("docs", snapshot.CollectionNames);
        Assert.Contains("images", snapshot.CollectionNames);
    }

    [Fact]
    public void Dispose_DisposesAllCollections()
    {
        var c1 = _manager.GetOrCreateCollection("docs", 3, DistanceMetric.Euclidean);
        var c2 = _manager.GetOrCreateCollection("images", 3, DistanceMetric.Euclidean);

        _manager.Dispose();

        var ex1 = Assert.Throws<AggregateException>(() => c1.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f })).Wait());
        Assert.IsType<ObjectDisposedException>(ex1.InnerException);

        var ex2 = Assert.Throws<AggregateException>(() => c2.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f })).Wait());
        Assert.IsType<ObjectDisposedException>(ex2.InnerException);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        _manager.Dispose();
        _manager.Dispose(); // Should not throw
    }

    [Fact]
    public void ConcurrentAccess_Safe()
    {
        System.Threading.Tasks.Parallel.For(0, 10, i =>
        {
            _manager.GetOrCreateCollection($"collection_{i}", 128, DistanceMetric.Cosine);
        });

        // Verify all collections exist
        for (int i = 0; i < 10; i++)
        {
            Assert.NotNull(_manager.GetCollection($"collection_{i}"));
        }
    }
}
