// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.Benchmarks;

/// <summary>
/// Head-to-Head Benchmark: VectorStore-Backends über <see cref="EmbeddedVectorStore"/>.
/// Misst Ingest, Enumeration, Point-Lookup und Suche mit identischer Workload
/// auf MvccBPlusTree vs. Legacy-BPlusTree-/BlobStore-Pfad.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class VectorStoreBackendBenchmark : IDisposable
{
    private const int Dimension = 128;
    private const int Count = 10_000;

    [Params(StorageBackend.MvccBPlusTree, StorageBackend.BPlusTree)]
    public StorageBackend Backend { get; set; }

    private string _dbPath = null!;
    private EmbeddedVectorStore? _store;
    private VectorCollection? _collection;

    private Vector[] _vectors = null!;
    private Vector _query;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var random = new Random(42);
        _vectors = new Vector[Count];
        for (int i = 0; i < Count; i++)
        {
            var data = new float[Dimension];
            for (int j = 0; j < Dimension; j++)
                data[j] = (float)random.NextDouble();
            _vectors[i] = new Vector(data);
        }

        var queryData = new float[Dimension];
        for (int j = 0; j < Dimension; j++)
            queryData[j] = (float)random.NextDouble();
        _query = new Vector(queryData);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_vecbench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbPath);

        var options = new StorageEngineOptions
        {
            RootPath = _dbPath,
            Backend = Backend,
            OverflowThresholdBytes = 256
        };
        _store = new EmbeddedVectorStore(options);

        _collection = _store.GetOrCreateCollection("bench", Dimension, DistanceMetric.Euclidean);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _store?.Dispose();
        _store = null;
        _collection = null;
        if (Directory.Exists(_dbPath))
        {
            try { Directory.Delete(_dbPath, recursive: true); } catch { }
        }
    }

    // ── Ingest (Bulk-Upsert) ──────────────────────────────────────────────────

    [Benchmark(Description = "Ingest: Bulk-Upsert 10k vectors")]
    public async Task IngestBulkUpsert()
    {
        var batch = _vectors.Select((v, i) => ((ulong)(i + 1), v, (VectorMetadata?)null)).ToList();
        await _collection!.PutBatchAsync(batch);
        await _store!.CheckpointAsync();
    }

    // ── Enumeration ─────────────────────────────────────────────────────────

    [Benchmark(Description = "Enumeration: EnumerateIdsAsync over 10k")]
    public async Task EnumerationOver10k()
    {
        var batch = _vectors.Select((v, i) => ((ulong)(i + 1), v, (VectorMetadata?)null)).ToList();
        await _collection!.PutBatchAsync(batch);
        await _store!.CheckpointAsync();

        int count = 0;
        await foreach (var id in _collection.EnumerateIdsAsync())
            count++;

        if (count != Count)
            throw new InvalidOperationException($"Expected {Count} items, got {count}");
    }

    // ── Point-Lookup ───────────────────────────────────────────────────────────

    [Benchmark(Description = "Point-Lookup: 1k random GetAsync")]
    public async Task PointLookup1k()
    {
        var batch = _vectors.Select((v, i) => ((ulong)(i + 1), v, (VectorMetadata?)null)).ToList();
        await _collection!.PutBatchAsync(batch);
        await _store!.CheckpointAsync();

        var random = new Random(42);
        for (int i = 0; i < 1000; i++)
        {
            var id = (ulong)(random.Next(Count) + 1);
            _ = await _collection.GetAsync(id);
        }
    }

    // ── Exact Search ─────────────────────────────────────────────────────────

    [Benchmark(Description = "Exact Search: Top-10 over 10k")]
    public async Task ExactSearchTop10()
    {
        var batch = _vectors.Select((v, i) => ((ulong)(i + 1), v, (VectorMetadata?)null)).ToList();
        await _collection!.PutBatchAsync(batch);
        await _store!.CheckpointAsync();

        var results = await _collection.SearchExactAsync(_query, topK: 10).ToListAsync();
        if (results.Count == 0)
            throw new InvalidOperationException("Search returned no results");
    }

    // ── HNSW Search ──────────────────────────────────────────────────────────

    [Benchmark(Description = "HNSW Search: Top-10 over 10k")]
    public async Task HnswSearchTop10()
    {
        var hnswCollection = _store!.GetOrCreateCollection("bench_hnsw", Dimension, DistanceMetric.Euclidean,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 8, EfConstruction = 64 });

        var batch = _vectors.Select((v, i) => ((ulong)(i + 1), v, (VectorMetadata?)null)).ToList();
        await hnswCollection.PutBatchAsync(batch);
        await _store.CheckpointAsync();

        var results = await hnswCollection.SearchHnswAsync(_query, topK: 10).ToListAsync();
        if (results.Count == 0)
            throw new InvalidOperationException("HNSW search returned no results");
    }

    public void Dispose()
    {
        IterationCleanup();
    }
}
