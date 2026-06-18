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

public class VectorCollectionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BlobStore _store;
    private readonly VectorCollectionManager _manager;

    public VectorCollectionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_test_{Guid.NewGuid()}");
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

    private Vector CreateVector(int dimension, int seed)
    {
        var random = new Random(seed);
        var data = new float[dimension];
        for (int i = 0; i < dimension; i++)
            data[i] = (float)random.NextDouble();
        return new Vector(data);
    }

    [Fact]
    public void Constructor_SetsProperties()
    {
        var collection = _manager.GetOrCreateCollection("test", 128, DistanceMetric.Cosine);

        Assert.Equal("test", collection.Name);
        Assert.Equal(128, collection.Dimension);
        Assert.Equal(DistanceMetric.Cosine, collection.DefaultMetric);
    }

    [Fact]
    public async Task PutAsync_StoresVector()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        await collection.PutAsync(1, vector);
        var entry = await collection.GetAsync(1);

        Assert.NotNull(entry);
        Assert.Equal(1ul, entry.Id);
        Assert.True(vector.Span.SequenceEqual(entry.Vector.Span));
    }

    [Fact]
    public async Task PutAsync_WithMetadata_StoresMetadata()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f, 3.0f });
        var metadata = new VectorMetadata { Id = 1, Collection = "test", Payload = new() { ["title"] = "Test" } };

        await collection.PutAsync(1, vector, metadata);
        var entry = await collection.GetAsync(1);

        Assert.NotNull(entry);
        Assert.NotNull(entry.Metadata);
        var titleValue = entry.Metadata.Payload!["title"];
        if (titleValue is System.Text.Json.JsonElement element)
            Assert.Equal("Test", element.GetString());
        else
            Assert.Equal("Test", titleValue);
    }

    [Fact]
    public async Task PutAsync_WrongDimension_ThrowsArgumentException()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f }); // Wrong dimension

        await Assert.ThrowsAsync<ArgumentException>(() => collection.PutAsync(1, vector));
    }

    [Fact]
    public async Task GetAsync_NonExisting_ReturnsNull()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        var entry = await collection.GetAsync(999);

        Assert.Null(entry);
    }

    [Fact]
    public async Task DeleteAsync_RemovesVector()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        await collection.PutAsync(1, vector);
        await collection.DeleteAsync(1);
        var entry = await collection.GetAsync(1);

        Assert.Null(entry);
    }

    [Fact]
    public async Task DeleteAsync_NonExisting_DoesNotThrow()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        await collection.DeleteAsync(999);

        // Should not throw
    }

    [Fact]
    public async Task ExistsAsync_Existing_ReturnsTrue()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        await collection.PutAsync(1, vector);
        var exists = await collection.ExistsAsync(1);

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_NonExisting_ReturnsFalse()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        var exists = await collection.ExistsAsync(999);

        Assert.False(exists);
    }

    [Fact]
    public async Task SearchExact_ReturnsTopK()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var v1 = new Vector(new float[] { 1.0f, 0.0f, 0.0f });
        var v2 = new Vector(new float[] { 0.0f, 1.0f, 0.0f });
        var v3 = new Vector(new float[] { 0.0f, 0.0f, 1.0f });

        await collection.PutAsync(1, v1);
        await collection.PutAsync(2, v2);
        await collection.PutAsync(3, v3);

        var query = new Vector(new float[] { 0.9f, 0.1f, 0.0f });
        var results = await collection.SearchExactAsync(query, 2).ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(1ul, results[0].Id); // Closest to v1
    }

    [Fact]
    public async Task SearchExact_EmptyCollection_ReturnsEmpty()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var query = new Vector(new float[] { 1.0f, 0.0f, 0.0f });

        var results = await collection.SearchExactAsync(query, 10).ToListAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchExact_CosineMetric_NormalizesAndReturnsCorrectOrder()
    {
        var collection = _manager.GetOrCreateCollection("test", 2, DistanceMetric.Cosine);
        var v1 = new Vector(new float[] { 1.0f, 0.0f }); // Already normalized
        var v2 = new Vector(new float[] { 0.0f, 1.0f });

        await collection.PutAsync(1, v1);
        await collection.PutAsync(2, v2);

        var query = new Vector(new float[] { 1.0f, 0.0f });
        var results = await collection.SearchExactAsync(query, 2).ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(1ul, results[0].Id); // Same direction as query
        Assert.True(results[0].Score < results[1].Score); // Lower score = closer
    }

    [Fact]
    public async Task SearchHnsw_WithIndex_ReturnsResults()
    {
        var collection = _manager.GetOrCreateCollection("test", 128, DistanceMetric.Cosine,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 8, RandomSeed = 42 });

        for (ulong i = 1; i <= 50; i++)
        {
            var vec = CreateVector(128, (int)i);
            await collection.PutAsync(i, vec);
        }

        await collection.RebuildIndexAsync(progress: null);

        var query = CreateVector(128, 25);
        var results = await collection.SearchHnswAsync(query, 10).ToListAsync();

        Assert.True(results.Count > 0);
        Assert.True(results.Count <= 10);
    }

    [Fact]
    public async Task SearchHnsw_WithoutIndex_ThrowsInvalidOperationException()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean, enableHnsw: false);
        var query = new Vector(new float[] { 1.0f, 0.0f, 0.0f });

        await Assert.ThrowsAsync<InvalidOperationException>(() => collection.SearchHnswAsync(query, 10).ToListAsync().AsTask());
    }

    [Fact]
    public async Task EnumerateIdsAsync_ReturnsAllIds()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        await collection.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));
        await collection.PutAsync(2, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));
        await collection.PutAsync(3, new Vector(new float[] { 0.0f, 0.0f, 1.0f }));

        var ids = await collection.EnumerateIdsAsync().ToListAsync();

        Assert.Equal(3, ids.Count);
        Assert.Contains(1ul, ids);
        Assert.Contains(2ul, ids);
        Assert.Contains(3ul, ids);
    }

    [Fact]
    public async Task EnumerateIdsAsync_EmptyCollection_ReturnsEmpty()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        var ids = await collection.EnumerateIdsAsync().ToListAsync();

        Assert.Empty(ids);
    }

    [Fact]
    public async Task RebuildIndexAsync_BuildsIndex()
    {
        var collection = _manager.GetOrCreateCollection("test", 128, DistanceMetric.Cosine,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 8, RandomSeed = 42 });

        for (ulong i = 1; i <= 20; i++)
        {
            var vec = CreateVector(128, (int)i);
            await collection.PutAsync(i, vec);
        }

        await collection.RebuildIndexAsync(progress: null);

        Assert.NotNull(collection.Index);
        Assert.True(collection.Index.NodeCount > 0);
    }

    [Fact]
    public async Task PutAsync_UpdatesExistingVector()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var v1 = new Vector(new float[] { 1.0f, 2.0f, 3.0f });
        var v2 = new Vector(new float[] { 4.0f, 5.0f, 6.0f });

        await collection.PutAsync(1, v1);
        await collection.PutAsync(1, v2);
        var entry = await collection.GetAsync(1);

        Assert.NotNull(entry);
        Assert.True(v2.Span.SequenceEqual(entry.Vector.Span));
    }

    [Fact]
    public async Task Count_IncreasesWithPuts()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        Assert.Equal(0, collection.Count);

        await collection.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));
        Assert.Equal(1, collection.Count);

        await collection.PutAsync(2, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));
        Assert.Equal(2, collection.Count);
    }

    [Fact]
    public async Task Count_DecreasesWithDeletes()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        await collection.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));
        await collection.PutAsync(2, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));
        await collection.DeleteAsync(1);

        Assert.Equal(1, collection.Count);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        collection.Dispose();
        collection.Dispose(); // Should not throw
    }

    [Fact]
    public async Task Operations_AfterDispose_ThrowObjectDisposedException()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        collection.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => collection.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f })));
    }

    [Fact]
    public async Task PutBatchAsync_StoresVectorsAndEnablesSearch()
    {
        var collection = _manager.GetOrCreateCollection("batch", 128, DistanceMetric.Cosine,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 8, RandomSeed = 42 });

        var items = new List<(ulong Id, Vector Vector, VectorMetadata? Metadata)>();
        for (ulong i = 1; i <= 50; i++)
        {
            items.Add((i, CreateVector(128, (int)i), null));
        }

        await collection.PutBatchAsync(items);

        Assert.Equal(50, collection.Count);

        // Exact Search
        var query = CreateVector(128, 25);
        var exactResults = await collection.SearchExactAsync(query, 5).ToListAsync();
        Assert.True(exactResults.Count > 0);

        // HNSW Search
        var hnswResults = await collection.SearchHnswAsync(query, 5).ToListAsync();
        Assert.True(hnswResults.Count > 0);
    }

    [Fact]
    public async Task PutBatchAsync_UpsertExisting_UpdatesVector()
    {
        var collection = _manager.GetOrCreateCollection("batch_upsert", 3, DistanceMetric.Euclidean);

        var v1 = new Vector(new float[] { 1.0f, 0.0f, 0.0f });
        var v2 = new Vector(new float[] { 0.0f, 1.0f, 0.0f });

        await collection.PutBatchAsync(new[]
        {
            (1ul, v1, (VectorMetadata?)null)
        });

        await collection.PutBatchAsync(new[]
        {
            (1ul, v2, (VectorMetadata?)null)
        });

        Assert.Equal(1, collection.Count);
        var entry = await collection.GetAsync(1);
        Assert.NotNull(entry);
        Assert.True(v2.Span.SequenceEqual(entry.Vector.Span));
    }
}
