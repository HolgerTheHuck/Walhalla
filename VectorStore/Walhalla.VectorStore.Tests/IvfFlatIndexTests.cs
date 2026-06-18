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

public class IvfFlatIndexTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BlobStore _store;
    private readonly VectorCollectionManager _manager;

    public IvfFlatIndexTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_ivf_test_{Guid.NewGuid()}");
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

    private static float[] CreateVector(int dimension, int seed)
    {
        var random = new Random(seed);
        var data = new float[dimension];
        for (int i = 0; i < dimension; i++)
            data[i] = (float)random.NextDouble();
        return data;
    }

    [Fact]
    public void Build_WithRandomVectors_CreatesClusters()
    {
        var index = new IvfFlatIndex(new IvfOptions { NClusters = 5, RandomSeed = 42 });
        var vectors = Enumerable.Range(1, 100)
            .Select(i => ((ulong)i, CreateVector(16, i)))
            .ToList();

        index.Build(vectors);

        Assert.True(index.IsBuilt);
        Assert.Equal(5, index.NClusters);
    }

    [Fact]
    public void SearchKnn_ReturnsCorrectTopK()
    {
        var index = new IvfFlatIndex(new IvfOptions { NClusters = 5, Nprobe = 5, RandomSeed = 42 });
        var vectors = Enumerable.Range(1, 100)
            .Select(i => ((ulong)i, CreateVector(16, i)))
            .ToList();

        index.Build(vectors);

        var query = CreateVector(16, 1); // Close to vector 1
        var results = index.SearchKnn(query, 5, id => vectors.First(v => v.Item1 == id).Item2);

        Assert.Equal(5, results.Count);
        // Vector 1 should be in the results (exact or very close match)
        Assert.Contains(results, r => r.Id == 1);
    }

    [Fact]
    public void Insert_AddsToCorrectCluster()
    {
        var index = new IvfFlatIndex(new IvfOptions { NClusters = 5, RandomSeed = 42 });
        var vectors = Enumerable.Range(1, 50)
            .Select(i => ((ulong)i, CreateVector(16, i)))
            .ToList();

        index.Build(vectors);
        index.Insert(999, CreateVector(16, 1));

        var query = CreateVector(16, 1);
        var results = index.SearchKnn(query, 10, id => id == 999 ? CreateVector(16, 1) : vectors.First(v => v.Item1 == id).Item2);

        Assert.Contains(results, r => r.Id == 999);
    }

    [Fact]
    public void Search_WithFilter_RespectsFilter()
    {
        var index = new IvfFlatIndex(new IvfOptions { NClusters = 5, Nprobe = 5, RandomSeed = 42 });
        var vectors = Enumerable.Range(1, 100)
            .Select(i => ((ulong)i, CreateVector(16, i)))
            .ToList();

        index.Build(vectors);

        var query = CreateVector(16, 1);
        var results = index.SearchKnn(query, 10, id => vectors.First(v => v.Item1 == id).Item2, isAllowed: id => id % 2 == 0);

        Assert.All(results, r => Assert.True(r.Id % 2 == 0));
    }

    [Fact]
    public void Clear_RemovesAllData()
    {
        var index = new IvfFlatIndex(new IvfOptions { NClusters = 5, RandomSeed = 42 });
        var vectors = Enumerable.Range(1, 50)
            .Select(i => ((ulong)i, CreateVector(16, i)))
            .ToList();

        index.Build(vectors);
        Assert.True(index.IsBuilt);

        index.Clear();
        Assert.False(index.IsBuilt);
        Assert.Equal(0, index.NClusters);
    }

    [Fact]
    public void SearchKnn_EmptyIndex_ReturnsEmpty()
    {
        var index = new IvfFlatIndex();
        var results = index.SearchKnn(CreateVector(16, 1), 5, _ => CreateVector(16, 1));
        Assert.Empty(results);
    }

    [Fact]
    public void Remove_DeletesFromClusters()
    {
        var index = new IvfFlatIndex(new IvfOptions { NClusters = 3, RandomSeed = 42 });
        var vectors = Enumerable.Range(1, 30)
            .Select(i => ((ulong)i, CreateVector(16, i)))
            .ToList();

        index.Build(vectors);
        index.Remove(1);

        var query = CreateVector(16, 1);
        var results = index.SearchKnn(query, 30, id => vectors.First(v => v.Item1 == id).Item2);

        Assert.DoesNotContain(results, r => r.Id == 1);
    }

    [Fact]
    public async Task Collection_SearchIvfAsync_AfterRebuild_ReturnsResults()
    {
        var collection = _manager.GetOrCreateCollection("test", 16, DistanceMetric.Euclidean, enableHnsw: false, enableIvf: true);

        var random = new Random(42);
        for (int i = 1; i <= 50; i++)
        {
            var vec = new Vector(CreateVector(16, i));
            await collection.PutAsync((ulong)i, vec);
        }

        await collection.RebuildIndexAsync(null);

        var query = new Vector(CreateVector(16, 1));
        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchIvfAsync(query, 5))
            results.Add(r);

        Assert.Equal(5, results.Count);
        Assert.Contains(results, r => r.Id == 1);
    }

    [Fact]
    public async Task Collection_SearchIvfAsync_WithNprobe_AdjustsRecall()
    {
        var collection = _manager.GetOrCreateCollection("test2", 16, DistanceMetric.Euclidean, enableHnsw: false, enableIvf: true);

        var random = new Random(42);
        for (int i = 1; i <= 100; i++)
        {
            var vec = new Vector(CreateVector(16, i));
            await collection.PutAsync((ulong)i, vec);
        }

        await collection.RebuildIndexAsync(null);

        var query = new Vector(CreateVector(16, 1));
        var resultsLow = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchIvfAsync(query, 10, nprobe: 1))
            resultsLow.Add(r);

        var resultsHigh = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchIvfAsync(query, 10, nprobe: 10))
            resultsHigh.Add(r);

        Assert.True(resultsLow.Count <= resultsHigh.Count, "Higher nprobe should return at least as many results");
        Assert.Equal(10, resultsHigh.Count);
    }

    [Fact]
    public async Task Collection_PutBatchAsync_UpdatesIvf()
    {
        var collection = _manager.GetOrCreateCollection("test3", 16, DistanceMetric.Euclidean, enableHnsw: false, enableIvf: true);

        var items = Enumerable.Range(1, 20)
            .Select(i => ((ulong)i, new Vector(CreateVector(16, i)), (VectorMetadata?)null))
            .ToList();

        await collection.PutBatchAsync(items);
        await collection.RebuildIndexAsync(null);

        var query = new Vector(CreateVector(16, 1));
        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchIvfAsync(query, 5))
            results.Add(r);

        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task Collection_DeleteAsync_RemovesFromIvf()
    {
        var collection = _manager.GetOrCreateCollection("test4", 16, DistanceMetric.Euclidean, enableHnsw: false, enableIvf: true);

        var vec = new Vector(CreateVector(16, 1));
        await collection.PutAsync(1, vec);
        await collection.RebuildIndexAsync(null);

        await collection.DeleteAsync(1);

        var query = new Vector(CreateVector(16, 1));
        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchIvfAsync(query, 10))
            results.Add(r);

        Assert.DoesNotContain(results, r => r.Id == 1);
    }

    [Fact]
    public void Build_AutoClusters_UsesSqrt()
    {
        var index = new IvfFlatIndex(new IvfOptions { NClusters = 0, RandomSeed = 42 });
        var vectors = Enumerable.Range(1, 100)
            .Select(i => ((ulong)i, CreateVector(16, i)))
            .ToList();

        index.Build(vectors);

        Assert.Equal(10, index.NClusters); // sqrt(100) = 10
    }
}
