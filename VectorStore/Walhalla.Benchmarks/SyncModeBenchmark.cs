// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Walhalla.Storage.Trees;
using Walhalla.Storage.Core.Configuration;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.Benchmarks;

/// <summary>
/// Vergleicht Ingest-Performance bei unterschiedlichen WAL-Sync-Modi.
/// </summary>
public class SyncModeBenchmark : IDisposable
{
    private const int Dimension = 1536;
    private readonly int[] _counts = [1_000, 5_000, 10_000];

    public async Task RunAll()
    {
        Console.WriteLine("========================================");
        Console.WriteLine("Walhalla SyncMode Performance Benchmark");
        Console.WriteLine("========================================");
        Console.WriteLine($"Dimension: {Dimension}");
        Console.WriteLine($"Sync modes: Fsync | WriteThrough | None");
        Console.WriteLine();

        foreach (var count in _counts)
        {
            await RunComparison(count);
            Console.WriteLine();
        }

        Console.WriteLine("========================================");
        Console.WriteLine("Benchmark complete.");
    }

    private async Task RunComparison(int count)
    {
        Console.WriteLine($"--- N = {count:N0} vectors ---");

        // Testdaten vorbereiten
        var random = new Random(42);
        var vectors = new List<Vector>(count);
        for (int i = 0; i < count; i++)
        {
            var floats = new float[Dimension];
            for (int j = 0; j < Dimension; j++)
                floats[j] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(floats.AsSpan());
            vectors.Add(new Vector(floats));
        }

        var batch = new List<(ulong Id, Vector Vector, VectorMetadata? Metadata)>(count);
        for (int i = 0; i < count; i++)
            batch.Add(((ulong)i, vectors[i], null));

        foreach (var mode in new[] { WalSyncMode.Fsync, WalSyncMode.WriteThrough, WalSyncMode.None })
        {
            var path = Path.Combine(Path.GetTempPath(), $"walhalla_sync_{mode}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);

            var options = new BlobStoreOptions(path)
            {
                WalSyncMode = mode,
                AutoCheckpointWalThresholdBytes = 0,
            };
            var store = new EmbeddedVectorStore(options);
            var collection = store.GetOrCreateCollection("bench", Dimension, DistanceMetric.Cosine, enableHnsw: false);

            var sw = Stopwatch.StartNew();
            await collection.PutBatchAsync(batch);
            sw.Stop();

            var ms = sw.ElapsedMilliseconds;
            var vecPerSec = count / (ms / 1000.0);
            Console.WriteLine($"  {mode,-13}: {ms,8:F1} ms  ({vecPerSec,8:F0} vec/s)");

            store.Dispose();
            Directory.Delete(path, recursive: true);
        }

        // Async HNSW Indexing + No Sync (Fire-and-Forget)
        {
            var path = Path.Combine(Path.GetTempPath(), $"walhalla_async_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);

            var options = new BlobStoreOptions(path)
            {
                WalSyncMode = WalSyncMode.None,
                AutoCheckpointWalThresholdBytes = 0,
            };
            var store = new EmbeddedVectorStore(options);
            var collection = store.GetOrCreateCollection("bench_async", Dimension, DistanceMetric.Cosine, enableHnsw: true,
                hnswOptions: new HnswOptions { M = 16, AsyncIndexing = true });

            var sw = Stopwatch.StartNew();
            await collection.PutBatchAsync(batch);
            // Nur Speichern messen – WaitForIndexingAsync weglassen = Fire-and-Forget
            await store.CheckpointAsync();
            sw.Stop();

            var ms = sw.ElapsedMilliseconds;
            var vecPerSec = count / (ms / 1000.0);
            Console.WriteLine($"  ASYNC+None   : {ms,8:F1} ms  ({vecPerSec,8:F0} vec/s)");

            store.Dispose();
            Directory.Delete(path, recursive: true);
        }
    }

    public void Dispose()
    {
    }
}
