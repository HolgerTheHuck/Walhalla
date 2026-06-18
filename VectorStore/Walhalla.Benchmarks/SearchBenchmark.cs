// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.Benchmarks;

/// <summary>
/// Benchmark für Vektor-Suche (Brute-Force vs HNSW).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class SearchBenchmark : IDisposable
{
    private const int Dimension = 1536;
    private const int VectorCount = 10_000;
    private const int TopK = 10;

    private string _dbPath = null!;
    private BlobStore _store = null!;
    private VectorCollection _collectionExact = null!;
    private VectorCollection _collectionHnsw = null!;
    private Walhalla.VectorStore.Vector _query;

    [Params(1_000, 10_000)]
    public int N;

    [GlobalSetup]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_search_bench_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbPath);

        _store = new BlobStore(new BlobStoreOptions(_dbPath));
        var manager = new VectorCollectionManager(_store);

        // Collection ohne HNSW (für exakte Suche)
        _collectionExact = manager.GetOrCreateCollection("exact", Dimension, DistanceMetric.Cosine, enableHnsw: false);

        // Collection mit HNSW
        _collectionHnsw = manager.GetOrCreateCollection("hnsw", Dimension, DistanceMetric.Cosine, enableHnsw: true,
            hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, EfSearch = 64 });

        // Vektoren generieren und einfügen
        var random = new Random(42);
        for (int i = 0; i < N; i++)
        {
            var floats = new float[Dimension];
            for (int j = 0; j < Dimension; j++)
                floats[j] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(floats.AsSpan());
            var vector = new Vector(floats);

            await _collectionExact.PutAsync((ulong)i, vector);
            await _collectionHnsw.PutAsync((ulong)i, vector);
        }

        // HNSW Index aufbauen
        await _collectionHnsw.RebuildIndexAsync(null);
        await _store.CheckpointAsync();

        // Query-Vektor
        var queryFloats = new float[Dimension];
        for (int j = 0; j < Dimension; j++)
            queryFloats[j] = (float)random.NextDouble();
        VectorDistance.NormalizeL2(queryFloats.AsSpan());
        _query = new Vector(queryFloats);
    }

    [Benchmark(Description = "Brute-Force Exact")]
    public async Task BruteForceSearch()
    {
        var results = await _collectionExact.SearchExactAsync(_query, TopK).ToListAsync();
    }

    [Benchmark(Description = "HNSW ANN", Baseline = true)]
    public async Task HnswSearch()
    {
        var results = await _collectionHnsw.SearchHnswAsync(_query, TopK, ef: 64).ToListAsync();
    }

    [Benchmark(Description = "HNSW ANN (ef=128)")]
    public async Task HnswSearchHighRecall()
    {
        var results = await _collectionHnsw.SearchHnswAsync(_query, TopK, ef: 128).ToListAsync();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }

    public void Dispose() => Cleanup();
}
