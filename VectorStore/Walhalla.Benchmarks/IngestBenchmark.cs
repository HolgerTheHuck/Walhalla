// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
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
/// Benchmark für Vektor-Ingest (Einfügegeschwindigkeit).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class IngestBenchmark : IDisposable
{
    private const int Dimension = 1536;
    private const int Count = 10_000;

    private string _dbPath = null!;
    private BlobStore _store = null!;
    private VectorCollection _collection = null!;
    private Vector[] _vectors = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_bench_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbPath);

        _store = new BlobStore(new BlobStoreOptions(_dbPath));
        var manager = new VectorCollectionManager(_store);
        _collection = manager.GetOrCreateCollection("bench", Dimension, DistanceMetric.Cosine, enableHnsw: false);

        // Vektoren vorbereiten
        var random = new Random(42);
        _vectors = new Vector[Count];
        for (int i = 0; i < Count; i++)
        {
            var floats = new float[Dimension];
            for (int j = 0; j < Dimension; j++)
                floats[j] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(floats.AsSpan());
            _vectors[i] = new Vector(floats);
        }
    }

    [Benchmark(Description = "Walhalla PutAsync (batch)")]
    public async Task WalhallaPutAsync()
    {
        for (int i = 0; i < Count; i++)
        {
            await _collection.PutAsync((ulong)i, _vectors[i]);
        }
        await _store.CheckpointAsync();
    }

    [Benchmark(Description = "Walhalla PutAsync (parallel)", Baseline = true)]
    public async Task WalhallaPutParallelAsync()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < Count; i++)
        {
            tasks.Add(_collection.PutAsync((ulong)i, _vectors[i]));
        }
        await Task.WhenAll(tasks);
        await _store.CheckpointAsync();
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
