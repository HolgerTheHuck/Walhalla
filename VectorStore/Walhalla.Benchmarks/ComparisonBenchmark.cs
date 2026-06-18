// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.Benchmarks;

/// <summary>
/// Head-to-Head Benchmark: Walhalla vs Qdrant.
/// Misst Ingest-Geschwindigkeit und Search-Performance mit Stopwatch.
/// </summary>
public class ComparisonBenchmark : IDisposable
{
    private const int Dimension = 1536;
    private readonly int[] _counts = [1_000, 10_000];

    public async Task RunAll()
    {
        Console.WriteLine("========================================");
        Console.WriteLine("Walhalla vs Qdrant Performance Benchmark");
        Console.WriteLine("========================================");
        Console.WriteLine($"Dimension: {Dimension}");
        Console.WriteLine($"Test sizes: {string.Join(", ", _counts)}");
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

        // Generate shared test data
        var random = new Random(42);
        var vectors = new List<Walhalla.VectorStore.Vector>(count);
        var qdrantVectors = new List<PointStruct>(count);

        for (int i = 0; i < count; i++)
        {
            var floats = new float[Dimension];
            for (int j = 0; j < Dimension; j++)
                floats[j] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(floats.AsSpan());

            vectors.Add(new Walhalla.VectorStore.Vector(floats));
            qdrantVectors.Add(new PointStruct
            {
                Id = (ulong)i,
                Vectors = floats
            });
        }

        var queryFloats = new float[Dimension];
        for (int j = 0; j < Dimension; j++)
            queryFloats[j] = (float)random.NextDouble();
        VectorDistance.NormalizeL2(queryFloats.AsSpan());
        var queryVector = new Walhalla.VectorStore.Vector(queryFloats);

        // ========== WALHALLA ==========
        var walhallaPath = Path.Combine(Path.GetTempPath(), $"walhalla_cmp_{Guid.NewGuid()}");
        Directory.CreateDirectory(walhallaPath);
        var walhallaStore = new BlobStore(new BlobStoreOptions(walhallaPath));
        var manager = new VectorCollectionManager(walhallaStore);
        var walhallaCollection = manager.GetOrCreateCollection("bench", Dimension, DistanceMetric.Cosine, enableHnsw: true,
            hnswOptions: new HnswOptions { M = 16 });

        // Ingest
        var sw = Stopwatch.StartNew();
        var batch = new List<(ulong Id, Walhalla.VectorStore.Vector Vector, VectorMetadata? Metadata)>(count);
        for (int i = 0; i < count; i++)
        {
            batch.Add(((ulong)i, vectors[i], null));
        }
        await walhallaCollection.PutBatchAsync(batch);
        await walhallaStore.CheckpointAsync();
        sw.Stop();
        var walhallaIngestMs = sw.ElapsedMilliseconds;

        // Exact Search
        sw.Restart();
        for (int i = 0; i < 10; i++)
        {
            var _ = await walhallaCollection.SearchExactAsync(queryVector, topK: 10).ToListAsync();
        }
        sw.Stop();
        var walhallaExactMs = sw.ElapsedMilliseconds / 10.0;

        // HNSW Search (Index wurde bereits während PutBatchAsync aufgebaut)

        // Recall-Test
        var exactResults = await walhallaCollection.SearchExactAsync(queryVector, topK: 10).ToListAsync();
        var hnswResults = await walhallaCollection.SearchHnswAsync(queryVector, topK: 10).ToListAsync();
        var exactIds = new HashSet<ulong>(exactResults.Select(r => r.Id));
        var hnswIds = new HashSet<ulong>(hnswResults.Select(r => r.Id));
        var recall = (double)exactIds.Intersect(hnswIds).Count() / exactIds.Count;
        Console.WriteLine($"    HNSW Recall@10: {recall:P1}");
        Console.WriteLine($"    Exact: {string.Join(", ", exactResults.Select(r => $"{r.Id}({r.Score:F3})"))}");
        Console.WriteLine($"    HNSW:  {string.Join(", ", hnswResults.Select(r => $"{r.Id}({r.Score:F3})"))}");
        Console.WriteLine($"    Index: NodeCount={walhallaCollection.Index?.NodeCount}, EntryLayer={walhallaCollection.Index?.EntryLayer}");
        
        sw.Restart();
        for (int i = 0; i < 100; i++)
        {
            var _ = await walhallaCollection.SearchHnswAsync(queryVector, topK: 10).ToListAsync();
        }
        sw.Stop();
        var walhallaHnswMs = sw.ElapsedMilliseconds / 100.0;

        walhallaStore.Dispose();
        Directory.Delete(walhallaPath, recursive: true);

        // ========== WALHALLA (skipHnswIndex + RebuildIndexAsync) ==========
        var walhallaPathFast = Path.Combine(Path.GetTempPath(), $"walhalla_fast_{Guid.NewGuid()}");
        Directory.CreateDirectory(walhallaPathFast);
        var walhallaStoreFast = new BlobStore(new BlobStoreOptions(walhallaPathFast));
        var managerFast = new VectorCollectionManager(walhallaStoreFast);
        var walhallaCollectionFast = managerFast.GetOrCreateCollection("bench_fast", Dimension, DistanceMetric.Cosine, enableHnsw: true,
            hnswOptions: new HnswOptions { M = 16 });

        sw.Restart();
        await walhallaCollectionFast.PutBatchAsync(batch, skipHnswIndex: true);
        await walhallaStoreFast.CheckpointAsync();
        sw.Stop();
        var walhallaFastIngestMs = sw.ElapsedMilliseconds;

        sw.Restart();
        await walhallaCollectionFast.RebuildIndexAsync(null);
        sw.Stop();
        var walhallaFastBuildMs = sw.ElapsedMilliseconds;

        var hnswResultsFast = await walhallaCollectionFast.SearchHnswAsync(queryVector, topK: 10).ToListAsync();
        var hnswIdsFast = new HashSet<ulong>(hnswResultsFast.Select(r => r.Id));
        var recallFast = (double)exactIds.Intersect(hnswIdsFast).Count() / exactIds.Count;
        Console.WriteLine($"    FAST HNSW Recall@10: {recallFast:P1}");
        Console.WriteLine($"    FAST Ingest:  {walhallaFastIngestMs} ms");
        Console.WriteLine($"    FAST Build:   {walhallaFastBuildMs} ms");
        Console.WriteLine($"    FAST Total:   {walhallaFastIngestMs + walhallaFastBuildMs} ms");

        walhallaStoreFast.Dispose();
        Directory.Delete(walhallaPathFast, recursive: true);

        // ========== WALHALLA (AsyncIndexing) ==========
        var walhallaPathAsync = Path.Combine(Path.GetTempPath(), $"walhalla_async_{Guid.NewGuid():N}");
        Directory.CreateDirectory(walhallaPathAsync);
        var walhallaStoreAsync = new BlobStore(new BlobStoreOptions(walhallaPathAsync));
        var managerAsync = new VectorCollectionManager(walhallaStoreAsync);
        var walhallaCollectionAsync = managerAsync.GetOrCreateCollection("bench_async", Dimension, DistanceMetric.Cosine, enableHnsw: true,
            hnswOptions: new HnswOptions { M = 16, AsyncIndexing = true });

        // Phase 1: Nur speichern (Fire-and-Forget)
        sw.Restart();
        await walhallaCollectionAsync.PutBatchAsync(batch);
        await walhallaStoreAsync.CheckpointAsync();
        sw.Stop();
        var walhallaAsyncStoreMs = sw.ElapsedMilliseconds;

        // Phase 2: HNSW-Worker im Hintergrund aufholen lassen
        sw.Restart();
        await walhallaCollectionAsync.WaitForIndexingAsync();
        sw.Stop();
        var walhallaAsyncIndexMs = sw.ElapsedMilliseconds;

        var hnswResultsAsync = await walhallaCollectionAsync.SearchHnswAsync(queryVector, topK: 10).ToListAsync();
        var hnswIdsAsync = new HashSet<ulong>(hnswResultsAsync.Select(r => r.Id));
        var recallAsync = (double)exactIds.Intersect(hnswIdsAsync).Count() / exactIds.Count;
        Console.WriteLine($"    ASYNC HNSW Recall@10: {recallAsync:P1}");
        Console.WriteLine($"    ASYNC Store:  {walhallaAsyncStoreMs} ms");
        Console.WriteLine($"    ASYNC Index:  {walhallaAsyncIndexMs} ms");
        Console.WriteLine($"    ASYNC Total:  {walhallaAsyncStoreMs + walhallaAsyncIndexMs} ms");

        sw.Restart();
        for (int i = 0; i < 100; i++)
        {
            var _ = await walhallaCollectionAsync.SearchHnswAsync(queryVector, topK: 10).ToListAsync();
        }
        sw.Stop();
        var walhallaAsyncHnswMs = sw.ElapsedMilliseconds / 100.0;

        walhallaStoreAsync.Dispose();
        Directory.Delete(walhallaPathAsync, recursive: true);

        // ========== QDRANT ==========
        var qdrantClient = new QdrantClient("localhost", 6334, https: false);
        var qdrantCollectionName = $"benchmark_{count}_{Guid.NewGuid():N}";

        try { await qdrantClient.DeleteCollectionAsync(qdrantCollectionName); } catch { }
        await qdrantClient.CreateCollectionAsync(
            qdrantCollectionName,
            new VectorParams { Size = (ulong)Dimension, Distance = Distance.Cosine },
            hnswConfig: new HnswConfigDiff { M = 16, EfConstruct = 200 });

        // Ingest
        sw.Restart();
        await qdrantClient.UpsertAsync(qdrantCollectionName, qdrantVectors);
        sw.Stop();
        var qdrantIngestMs = sw.ElapsedMilliseconds;

        // Exact Search
        sw.Restart();
        for (int i = 0; i < 10; i++)
        {
            var _ = await qdrantClient.SearchAsync(qdrantCollectionName, queryFloats, limit: 10, searchParams: new SearchParams { Exact = true });
        }
        sw.Stop();
        var qdrantExactMs = sw.ElapsedMilliseconds / 10.0;

        // HNSW Search
        sw.Restart();
        for (int i = 0; i < 100; i++)
        {
            var _ = await qdrantClient.SearchAsync(qdrantCollectionName, queryFloats, limit: 10, searchParams: new SearchParams { HnswEf = 64 });
        }
        sw.Stop();
        var qdrantHnswMs = sw.ElapsedMilliseconds / 100.0;

        try { await qdrantClient.DeleteCollectionAsync(qdrantCollectionName); } catch { }

        // ========== RESULTS ==========
        Console.WriteLine($"  Ingest (Standard - inkrementeller HNSW-Build):");
        Console.WriteLine($"    Walhalla: {walhallaIngestMs,8:F1} ms  ({count / (walhallaIngestMs / 1000.0):F0} vec/s)");
        Console.WriteLine($"    Qdrant:   {qdrantIngestMs,8:F1} ms  ({count / (qdrantIngestMs / 1000.0):F0} vec/s)");
        Console.WriteLine($"    Ratio:    {(double)walhallaIngestMs / qdrantIngestMs:F2}x (Walhalla ist {(double)walhallaIngestMs / qdrantIngestMs:F1}x langsamer)");

        Console.WriteLine($"  Ingest (FAST - skipHnswIndex + RebuildIndexAsync):");
        Console.WriteLine($"    Speichern:  {walhallaFastIngestMs,8:F1} ms  ({count / (walhallaFastIngestMs / 1000.0):F0} vec/s)");
        Console.WriteLine($"    Index-Build:{walhallaFastBuildMs,8:F1} ms");
        Console.WriteLine($"    Gesamt:     {walhallaFastIngestMs + walhallaFastBuildMs,8:F1} ms  ({count / ((walhallaFastIngestMs + walhallaFastBuildMs) / 1000.0):F0} vec/s)");
        Console.WriteLine($"    Qdrant:     {qdrantIngestMs,8:F1} ms");
        Console.WriteLine($"    FAST Ratio: {(double)(walhallaFastIngestMs + walhallaFastBuildMs) / qdrantIngestMs:F2}x (Walhalla FAST ist {(double)(walhallaFastIngestMs + walhallaFastBuildMs) / qdrantIngestMs:F1}x langsamer)");

        Console.WriteLine($"  Ingest (ASYNC - Fire-and-Forget HNSW):");
        Console.WriteLine($"    Speichern:      {walhallaAsyncStoreMs,8:F1} ms  ({count / (walhallaAsyncStoreMs / 1000.0):F0} vec/s)");
        Console.WriteLine($"    Index-Build:    {walhallaAsyncIndexMs,8:F1} ms");
        Console.WriteLine($"    Gesamt:         {walhallaAsyncStoreMs + walhallaAsyncIndexMs,8:F1} ms  ({count / ((walhallaAsyncStoreMs + walhallaAsyncIndexMs) / 1000.0):F0} vec/s)");
        Console.WriteLine($"    Qdrant:         {qdrantIngestMs,8:F1} ms");
        Console.WriteLine($"    ASYNC Ratio:    {(double)(walhallaAsyncStoreMs + walhallaAsyncIndexMs) / qdrantIngestMs:F2}x (Walhalla ASYNC ist {(double)(walhallaAsyncStoreMs + walhallaAsyncIndexMs) / qdrantIngestMs:F1}x langsamer)");

        Console.WriteLine($"  Exact Search (avg of 10):");
        Console.WriteLine($"    Walhalla: {walhallaExactMs,8:F2} ms");
        Console.WriteLine($"    Qdrant:   {qdrantExactMs,8:F2} ms");
        Console.WriteLine($"    Ratio:    {(double)qdrantExactMs / walhallaExactMs:F2}x (Qdrant ist {(double)qdrantExactMs / walhallaExactMs:F1}x langsamer)");

        Console.WriteLine($"  HNSW Search (avg of 100, ef=64):");
        Console.WriteLine($"    Walhalla:       {walhallaHnswMs,8:F2} ms  (Recall {recall:P0})");
        Console.WriteLine($"    Walhalla FAST:  {walhallaHnswMs:F2} ms  (Recall {recallFast:P0})");
        Console.WriteLine($"    Walhalla ASYNC: {walhallaAsyncHnswMs,8:F2} ms  (Recall {recallAsync:P0})");
        Console.WriteLine($"    Qdrant:         {qdrantHnswMs,8:F2} ms");
        Console.WriteLine($"    Ratio:          {(double)qdrantHnswMs / walhallaHnswMs:F2}x (Qdrant ist {(double)qdrantHnswMs / walhallaHnswMs:F1}x langsamer)");
    }

    public void Dispose()
    {
    }
}
