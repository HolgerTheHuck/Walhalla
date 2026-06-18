// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;
using Xunit;
using Xunit.Abstractions;

namespace Walhalla.VectorStore.Tests;

public class PerformanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dbPath;
    private readonly BlobStore _store;
    private readonly VectorCollectionManager _manager;

    public PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_perf_{Guid.NewGuid()}");
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
    public void SimdDistance_Throughput()
    {
        _output.WriteLine("=== SIMD Distance Throughput ===");
        var dims = new[] { 128, 384, 768, 1536 };
        const int iterations = 500_000;

        foreach (var dim in dims)
        {
            var a = new float[dim];
            var b = new float[dim];
            new Random(42).NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
            new Random(43).NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan()));

            // Warmup
            for (int i = 0; i < 1000; i++)
            {
                VectorDistance.Euclidean(a, b);
                VectorDistance.DotProduct(a, b);
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                VectorDistance.Euclidean(a, b);
            sw.Stop();
            var euclideanOps = iterations / sw.Elapsed.TotalSeconds;

            sw.Restart();
            for (int i = 0; i < iterations; i++)
                VectorDistance.DotProduct(a, b);
            sw.Stop();
            var dotOps = iterations / sw.Elapsed.TotalSeconds;

            sw.Restart();
            for (int i = 0; i < iterations; i++)
                VectorDistance.Cosine(a, b);
            sw.Stop();
            var cosineOps = iterations / sw.Elapsed.TotalSeconds;

            _output.WriteLine($"Dim {dim,4}: Euclidean {euclideanOps/1e6:F1}M ops/s, DotProduct {dotOps/1e6:F1}M ops/s, Cosine {cosineOps/1e6:F1}M ops/s");
        }
    }

    [Fact]
    public void HnswIndex_InsertAndSearch()
    {
        _output.WriteLine("=== HNSW Index Performance ===");
        var configs = new[] { (dim: 128, count: 1000), (dim: 384, count: 5000), (dim: 768, count: 10000) };

        foreach (var (dim, count) in configs)
        {
            var index = new HnswIndex(new HnswOptions { M = 16, EfConstruction = 200 });
            var random = new Random(42);
            var vectors = new float[count][];
            for (int i = 0; i < count; i++)
            {
                vectors[i] = new float[dim];
                random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vectors[i].AsSpan()));
            }

            // Insert benchmark
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                var v = vectors[i];
                index.Insert((ulong)i, _ => v);
            }
            sw.Stop();
            var insertOps = count / sw.Elapsed.TotalSeconds;

            // Search benchmark
            var query = vectors[0];
            sw.Restart();
            for (int i = 0; i < 1000; i++)
                index.SearchKnn(_ => query, 10);
            sw.Stop();
            var searchOps = 1000 / sw.Elapsed.TotalSeconds;

            _output.WriteLine($"Dim {dim,3} x {count,5}: Insert {insertOps:F0} ops/s ({sw.Elapsed.TotalMilliseconds:F0}ms total), Search {searchOps:F0} qps, Nodes: {index.NodeCount}");
        }
    }

    [Fact]
    public async Task VectorCollection_Throughput()
    {
        _output.WriteLine("=== VectorCollection Throughput ===");
        var collection = _manager.GetOrCreateCollection("bench", 128, DistanceMetric.Cosine);
        var random = new Random(42);

        // Put benchmark
        int putCount = 5000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < putCount; i++)
        {
            var vec = new float[128];
            random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vec.AsSpan()));
            await collection.PutAsync((ulong)i, new Vector(vec));
        }
        sw.Stop();
        var putOps = putCount / sw.Elapsed.TotalSeconds;
        _output.WriteLine($"Put {putCount} vectors: {putOps:F0} ops/s ({sw.Elapsed.TotalMilliseconds:F1}ms)");

        // Get benchmark
        sw.Restart();
        for (int i = 0; i < 1000; i++)
            await collection.GetAsync((ulong)(i % putCount));
        sw.Stop();
        var getOps = 1000 / sw.Elapsed.TotalSeconds;
        _output.WriteLine($"Get 1000 vectors: {getOps:F0} ops/s ({sw.Elapsed.TotalMilliseconds:F1}ms)");

        // Exact search benchmark
        var queryVec = new float[128];
        random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(queryVec.AsSpan()));
        var query = new Vector(queryVec);

        sw.Restart();
        var exactResults = await collection.SearchExactAsync(query, 10).ToListAsync();
        sw.Stop();
        _output.WriteLine($"ExactSearch top-10: {sw.Elapsed.TotalMilliseconds:F1}ms ({exactResults.Count} results)");

        // HNSW search benchmark
        if (collection.Index is not null)
        {
            sw.Restart();
            for (int i = 0; i < 100; i++)
                await collection.SearchHnswAsync(query, 10).ToListAsync();
            sw.Stop();
            var hnswQps = 100 / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"HNSW Search 100x top-10: {hnswQps:F0} qps ({sw.Elapsed.TotalMilliseconds:F1}ms)");
        }
    }
}
