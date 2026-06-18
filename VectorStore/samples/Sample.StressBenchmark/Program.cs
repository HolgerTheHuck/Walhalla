// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;
using Vector = Walhalla.VectorStore.Vector;

// ============================================================================
// Walhalla vs Qdrant: Comprehensive Stress Benchmark
// Tests: Reliability, Performance, Recall Quality, Concurrent Access
// ============================================================================

var runner = new StressBenchmark();

await runner.RunAll();

Console.WriteLine();
Console.WriteLine("========================================");
Console.WriteLine("STRESS BENCHMARK COMPLETE");
Console.WriteLine("========================================");

// ============================================================================
// Benchmark Implementation
// ============================================================================

public class StressBenchmark
{
    private static readonly (int count, string label)[] Scales = [(1_000, "1K"), (10_000, "10K"), (50_000, "50K")];
    private const int Dimension = 1536;
    private const int QueryCount = 100;
    private const int ReliabilityRuns = 5;
    private const int ConcurrentReaders = 8;
    private const int ConcurrentIterations = 100;

    private readonly QdrantClient _qdrant = new("localhost", 6334, https: false);

    public async Task RunAll()
    {
        PrintHeader();

        // === SECTION 1: Reliability ===
        await Section1_RepeatedRunConsistency();
        await Section2_CloseReopenIntegrity();
        await Section3_BatchUpsertCorrectness();

        // === SECTION 2: Performance @ Scale ===
        foreach (var (count, label) in Scales)
        {
            await Section4_ScalePerformance(count, label);
        }

        // === SECTION 3: HNSW Recall Quality ===
        await Section5_RecallVsEf();

        // === SECTION 4: Concurrent Stress ===
        await Section6_ConcurrentReadWrite();

        // === SECTION 5: Summary ===
        PrintSummary();
    }

    // ========================================================================
    // SECTION 1: Repeated Run Consistency (Reliability)
    // ========================================================================
    private async Task Section1_RepeatedRunConsistency()
    {
        Console.WriteLine("\n========================================");
        Console.WriteLine("SECTION 1: REPEATED RUN CONSISTENCY");
        Console.WriteLine("  5 identische Laeufe, 1000 Vektoren, 1536 dim");
        Console.WriteLine("  Erwartet: Gleiche Recall-Werte, stabile Latenzen");
        Console.WriteLine("========================================\n");

        var runs = new List<(double recall, double exactMs, double hnswMs)>();
        var groundTruth = new List<ulong>[ReliabilityRuns];

        for (int run = 0; run < ReliabilityRuns; run++)
        {
            var (vectors, queryVec, _) = GenerateData(1_000, run * 100);

            var path = Path.Combine(Path.GetTempPath(), $"wl_stress_r1_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);

            var store = new BlobStore(new BlobStoreOptions(path));
            var manager = new VectorCollectionManager(store);
            var coll = manager.GetOrCreateCollection("r1", Dimension, DistanceMetric.Cosine,
                enableHnsw: true, hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, AutoScale = false });

            var batch = vectors.Select((v, i) => ((ulong)i, v, (VectorMetadata?)null)).ToList();
            await coll.PutBatchAsync(batch);

            var exactResults = await coll.SearchExactAsync(queryVec, topK: 10).ToListAsync();
            var exactIds = new HashSet<ulong>(exactResults.Select(r => r.Id));
            groundTruth[run] = exactResults.Select(r => r.Id).ToList();

            var hnswResults = await coll.SearchHnswAsync(queryVec, topK: 10).ToListAsync();
            var recall = (double)exactIds.Intersect(hnswResults.Select(r => r.Id)).Count() / exactIds.Count;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < QueryCount; i++)
                await coll.SearchExactAsync(queryVec, topK: 10).ToListAsync();
            sw.Stop();
            var exactMs = sw.ElapsedMilliseconds / (double)QueryCount;

            sw.Restart();
            for (int i = 0; i < QueryCount; i++)
                await coll.SearchHnswAsync(queryVec, topK: 10).ToListAsync();
            sw.Stop();
            var hnswMs = sw.ElapsedMilliseconds / (double)QueryCount;

            runs.Add((recall, exactMs, hnswMs));
            Console.WriteLine($"  Run {run + 1}: Recall@10={recall:P0}  Exact={exactMs:F2}ms  HNSW={hnswMs:F2}ms");

            store.Dispose();
            Directory.Delete(path, recursive: true);
        }

        bool gtConsistent = true;
        for (int i = 1; i < ReliabilityRuns; i++)
        {
            if (!groundTruth[0].SequenceEqual(groundTruth[i]))
            {
                gtConsistent = false;
                Console.WriteLine($"  WARNUNG: Ground Truth Run 0 != Run {i}");
            }
        }

        var recalls = runs.Select(r => r.recall).ToList();
        var exactLatencies = runs.Select(r => r.exactMs).ToList();
        var hnswLatencies = runs.Select(r => r.hnswMs).ToList();

        Console.WriteLine($"\n  Ground Truth konsistent: {(gtConsistent ? "JA" : "NEIN - PROBLEM!")}");
        Console.WriteLine($"  Recall@10:   min={recalls.Min():P0}  max={recalls.Max():P0}  avg={recalls.Average():P0}  stddev={StdDev(recalls):P1}");
        Console.WriteLine($"  Exact (ms):  min={exactLatencies.Min():F2}  max={exactLatencies.Max():F2}  avg={exactLatencies.Average():F2}  stddev={StdDev(exactLatencies):F2}");
        Console.WriteLine($"  HNSW (ms):   min={hnswLatencies.Min():F2}  max={hnswLatencies.Max():F2}  avg={hnswLatencies.Average():F2}  stddev={StdDev(hnswLatencies):F2}");

        _results.Add(("Reliability: Ground Truth", gtConsistent ? "PASS" : "FAIL"));
        _results.Add(("Reliability: Recall Variant", StdDev(recalls) < 0.1 ? "PASS" : $"WARN (stddev={StdDev(recalls):P1})"));
    }

    // ========================================================================
    // SECTION 2: Close/Reopen Integrity
    // ========================================================================
    private async Task Section2_CloseReopenIntegrity()
    {
        Console.WriteLine("\n========================================");
        Console.WriteLine("SECTION 2: CLOSE/REOPEN INTEGRITY");
        Console.WriteLine("  1000 Vektoren speichern, schliessen, wieder oeffnen");
        Console.WriteLine("  Erwartet: Alle Daten korrekt lesbar, HNSW wieder nutzbar");
        Console.WriteLine("========================================\n");

        var (vectors, queryVec, _) = GenerateData(1_000, 42);
        var path = Path.Combine(Path.GetTempPath(), $"wl_stress_r2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        List<ulong> groundTruthIds;
        int countAfterReopen;
        double recallAfterReopen;
        string getResult;

        // Phase 1: Write
        {
            var store = new BlobStore(new BlobStoreOptions(path));
            var manager = new VectorCollectionManager(store);
            var coll = manager.GetOrCreateCollection("r2", Dimension, DistanceMetric.Cosine,
                enableHnsw: true, hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, AutoScale = false });

            var batch = vectors.Select((v, i) => ((ulong)i, v, (VectorMetadata?)null)).ToList();
            await coll.PutBatchAsync(batch);

            var exact = await coll.SearchExactAsync(queryVec, topK: 10).ToListAsync();
            groundTruthIds = exact.Select(r => r.Id).ToList();
            Console.WriteLine($"  Phase 1 (Write): {vectors.Count} Vektoren gespeichert");
            Console.WriteLine($"  Ground Truth: [{string.Join(", ", groundTruthIds)}]");

            store.Dispose();
        }

        // Phase 2: Reopen
        {
            var store = new BlobStore(new BlobStoreOptions(path));
            var manager = new VectorCollectionManager(store);
            var coll = manager.GetOrCreateCollection("r2", Dimension, DistanceMetric.Cosine,
                enableHnsw: true, hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, AutoScale = false });

            countAfterReopen = await coll.CountAsync();

            var exact = await coll.SearchExactAsync(queryVec, topK: 10).ToListAsync();
            var exactIds = exact.Select(r => r.Id).ToList();
            bool exactMatch = groundTruthIds.SequenceEqual(exactIds);

            await coll.RebuildIndexAsync(null);
            var hnsw = await coll.SearchHnswAsync(queryVec, topK: 10).ToListAsync();
            var hnswIds = hnsw.Select(r => r.Id).ToHashSet();
            recallAfterReopen = (double)groundTruthIds.Intersect(hnswIds).Count() / groundTruthIds.Count;

            var entry = await coll.GetAsync(groundTruthIds[0]);
            getResult = entry is not null
                ? $"OK ({entry.Vector.Dimension} dim)"
                : "NICHT GEFUNDEN!";

            Console.WriteLine($"  Phase 2 (Reopen): Count={countAfterReopen}");
            Console.WriteLine($"  Exact Match: {(exactMatch ? "JA" : "NEIN - PROBLEM!")}");
            Console.WriteLine($"  HNSW Recall@10: {recallAfterReopen:P0}");
            Console.WriteLine($"  Exact: [{string.Join(", ", exactIds)}]");
            Console.WriteLine($"  HNSW:  [{string.Join(", ", hnswIds)}]");
            Console.WriteLine($"  Get({groundTruthIds[0]}): Vector {getResult}");

            store.Dispose();
        }

        Directory.Delete(path, recursive: true);

        bool passed = countAfterReopen == 1_000 && recallAfterReopen > 0.8;
        _results.Add(("Close/Reopen: Count", countAfterReopen == 1_000 ? "PASS" : $"FAIL ({countAfterReopen})"));
        _results.Add(("Close/Reopen: Recall", recallAfterReopen > 0.8 ? "PASS" : $"FAIL ({recallAfterReopen:P0})"));
        _results.Add(("Close/Reopen: Get", getResult.Contains("OK") ? "PASS" : "FAIL"));
    }

    // ========================================================================
    // SECTION 3: Batch Upsert Correctness
    // ========================================================================
    private async Task Section3_BatchUpsertCorrectness()
    {
        Console.WriteLine("\n========================================");
        Console.WriteLine("SECTION 3: BATCH UPSERT CORRECTNESS");
        Console.WriteLine("  1000 Vektoren, dann gleiche IDs mit anderen Werten ueberschreiben");
        Console.WriteLine("  Erwartet: Neue Werte, keine Duplikate, Count stabil");
        Console.WriteLine("========================================\n");

        var path = Path.Combine(Path.GetTempPath(), $"wl_stress_r3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        var store = new BlobStore(new BlobStoreOptions(path));
        var manager = new VectorCollectionManager(store);
        var coll = manager.GetOrCreateCollection("r3", Dimension, DistanceMetric.Cosine,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 16, AutoScale = false });

        var random = new Random(42);
        var batch1 = new List<(ulong, Vector, VectorMetadata?)>();
        var batch2 = new List<(ulong, Vector, VectorMetadata?)>();

        for (int i = 0; i < 1_000; i++)
        {
            var v1 = RandomVector(random, Dimension);
            var v2 = RandomVector(random, Dimension);
            batch1.Add(((ulong)i, v1, null));
            var meta = new VectorMetadata
            {
                Id = (ulong)i,
                Collection = "r3",
                Payload = new Dictionary<string, object> { { "version", 2 } }
            };
            batch2.Add(((ulong)i, v2, meta));
        }

        await coll.PutBatchAsync(batch1);
        var count1 = await coll.CountAsync();
        var entry1 = await coll.GetAsync(42);

        await coll.PutBatchAsync(batch2);
        var count2 = await coll.CountAsync();
        var entry2 = await coll.GetAsync(42);

        bool countOk = count1 == 1_000 && count2 == 1_000;
        bool vecChanged = entry1 is not null && entry2 is not null &&
            !entry1.Vector.Span.SequenceEqual(entry2.Vector.Span);
        bool metaChanged = entry1?.Metadata is null && entry2?.Metadata is not null;

        Console.WriteLine($"  Nach Batch 1: Count={count1}, Get(42)={entry1?.Vector.Dimension}dim, Meta={entry1?.Metadata?.Payload?.Count ?? 0} keys");
        Console.WriteLine($"  Nach Batch 2: Count={count2}, Get(42)={entry2?.Vector.Dimension}dim, Meta={entry2?.Metadata?.Payload?.Count ?? 0} keys");
        Console.WriteLine($"  Count stabil: {(countOk ? "JA" : "NEIN")}");
        Console.WriteLine($"  Vektor geaendert: {(vecChanged ? "JA" : "NEIN")}");
        Console.WriteLine($"  Metadata neu gesetzt: {(metaChanged ? "JA" : "NEIN")}");

        var ids = await coll.EnumerateIdsAsync().ToListAsync();
        bool noDupes = ids.Count == ids.Distinct().Count();
        Console.WriteLine($"  Keine Duplikate: {(noDupes ? "JA" : "NEIN")} ({ids.Count} IDs)");

        store.Dispose();
        Directory.Delete(path, recursive: true);

        _results.Add(("Upsert: Count Stable", countOk ? "PASS" : "FAIL"));
        _results.Add(("Upsert: Vector Changed", vecChanged ? "PASS" : "FAIL"));
        _results.Add(("Upsert: Metadata", metaChanged ? "PASS" : "FAIL"));
        _results.Add(("Upsert: No Duplicates", noDupes ? "PASS" : "FAIL"));
    }

    // ========================================================================
    // SECTION 4: Scale Performance
    // ========================================================================
    private async Task Section4_ScalePerformance(int count, string label)
    {
        Console.WriteLine($"\n========================================");
        Console.WriteLine($"SECTION 4.{label}: SCALE PERFORMANCE ({count:N0} vectors, {Dimension} dim)");
        Console.WriteLine($"========================================\n");

        var (vectors, queryVec, queryFloat) = GenerateData(count, 42);

        // ---------- WALHALLA ----------
        var wlPath = Path.Combine(Path.GetTempPath(), $"wl_stress_s4_{Guid.NewGuid():N}");
        Directory.CreateDirectory(wlPath);

        var wlStore = new BlobStore(new BlobStoreOptions(wlPath));
        var wlManager = new VectorCollectionManager(wlStore);
        var wlColl = wlManager.GetOrCreateCollection("s4", Dimension, DistanceMetric.Cosine,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, AutoScale = false });

        var batch = vectors.Select((v, i) => ((ulong)i, v, (VectorMetadata?)null)).ToList();

        // Ingest Standard
        var sw = Stopwatch.StartNew();
        await wlColl.PutBatchAsync(batch);
        sw.Stop();
        long wlIngestStandardMs = sw.ElapsedMilliseconds;
        double wlIngestRate = count / (wlIngestStandardMs / 1000.0);

        // Exact Ground Truth
        var exactResults = await wlColl.SearchExactAsync(queryVec, topK: 10).ToListAsync();
        var exactIds = new HashSet<ulong>(exactResults.Select(r => r.Id));

        // HNSW Recall & Latency
        sw.Restart();
        for (int i = 0; i < QueryCount; i++)
            await wlColl.SearchHnswAsync(queryVec, topK: 10).ToListAsync();
        sw.Stop();
        double wlHnswMs = sw.ElapsedMilliseconds / (double)QueryCount;

        var hnswResults = await wlColl.SearchHnswAsync(queryVec, topK: 10).ToListAsync();
        double wlRecall = (double)exactIds.Intersect(hnswResults.Select(r => r.Id)).Count() / exactIds.Count;

        // Exact Latency
        sw.Restart();
        for (int i = 0; i < Math.Min(10, QueryCount); i++)
            await wlColl.SearchExactAsync(queryVec, topK: 10).ToListAsync();
        sw.Stop();
        double wlExactMs = sw.ElapsedMilliseconds / (double)Math.Min(10, QueryCount);

        long wlDiskSize = GetDirectorySize(wlPath);
        long wlNodeCount = wlColl.Index?.NodeCount ?? 0;

        Console.WriteLine($"  --- Walhalla ---");
        Console.WriteLine($"  Ingest (Standard):   {wlIngestStandardMs,8} ms  ({wlIngestRate:F0} vec/s)");
        Console.WriteLine($"  HNSW Recall@10:      {wlRecall:P1}");
        Console.WriteLine($"  HNSW Search (avg):   {wlHnswMs:F2} ms");
        Console.WriteLine($"  Exact Search (avg):  {wlExactMs:F2} ms");
        Console.WriteLine($"  Index Nodes:         {wlNodeCount}");
        Console.WriteLine($"  Disk:                {wlDiskSize / 1024.0 / 1024.0:F1} MB");

        wlStore.Dispose();
        Directory.Delete(wlPath, recursive: true);

        // ---------- WALHALLA FAST (skipHnswIndex + Rebuild) ----------
        var wlFastPath = Path.Combine(Path.GetTempPath(), $"wl_stress_s4f_{Guid.NewGuid():N}");
        Directory.CreateDirectory(wlFastPath);
        var wlFastStore = new BlobStore(new BlobStoreOptions(wlFastPath));
        var wlFastManager = new VectorCollectionManager(wlFastStore);
        var wlFastColl = wlFastManager.GetOrCreateCollection("s4f", Dimension, DistanceMetric.Cosine,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, AutoScale = false });

        sw.Restart();
        await wlFastColl.PutBatchAsync(batch, skipHnswIndex: true);
        sw.Stop();
        long wlFastStoreMs = sw.ElapsedMilliseconds;

        sw.Restart();
        await wlFastColl.RebuildIndexAsync(null);
        sw.Stop();
        long wlFastBuildMs = sw.ElapsedMilliseconds;

        var hnswFastResults = await wlFastColl.SearchHnswAsync(queryVec, topK: 10).ToListAsync();
        double wlFastRecall = (double)exactIds.Intersect(hnswFastResults.Select(r => r.Id)).Count() / exactIds.Count;

        Console.WriteLine($"  --- Walhalla FAST ---");
        Console.WriteLine($"  Store Only:          {wlFastStoreMs,8} ms  ({count / (wlFastStoreMs / 1000.0):F0} vec/s)");
        Console.WriteLine($"  RebuildIndex:        {wlFastBuildMs,8} ms");
        Console.WriteLine($"  Total:               {wlFastStoreMs + wlFastBuildMs,8} ms  ({count / ((wlFastStoreMs + wlFastBuildMs) / 1000.0):F0} vec/s)");
        Console.WriteLine($"  Recall@10:           {wlFastRecall:P1}");

        wlFastStore.Dispose();
        Directory.Delete(wlFastPath, recursive: true);

        // ---------- QDRANT ----------
        try
        {
            var qdrantCollection = $"stress_s4_{Guid.NewGuid():N}";
            try { await _qdrant.DeleteCollectionAsync(qdrantCollection); } catch { }

            await _qdrant.CreateCollectionAsync(qdrantCollection,
                new VectorParams { Size = (ulong)Dimension, Distance = Distance.Cosine },
                hnswConfig: new HnswConfigDiff { M = 16, EfConstruct = 200 });

            var qdrantPoints = vectors.Select((v, i) => new PointStruct
            {
                Id = (ulong)i,
                Vectors = v.Span.ToArray()
            }).ToList();

            sw.Restart();
            await _qdrant.UpsertAsync(qdrantCollection, qdrantPoints);
            sw.Stop();
            long qdIngestMs = sw.ElapsedMilliseconds;

            // HNSW Search
            sw.Restart();
            for (int i = 0; i < QueryCount; i++)
                await _qdrant.SearchAsync(qdrantCollection, queryFloat, limit: 10,
                    searchParams: new SearchParams { HnswEf = 64 });
            sw.Stop();
            double qdHnswMs = sw.ElapsedMilliseconds / (double)QueryCount;

            // Exact Search
            sw.Restart();
            for (int i = 0; i < 10; i++)
                await _qdrant.SearchAsync(qdrantCollection, queryFloat, limit: 10,
                    searchParams: new SearchParams { Exact = true });
            sw.Stop();
            double qdExactMs = sw.ElapsedMilliseconds / 10.0;

            Console.WriteLine($"  --- Qdrant ---");
            Console.WriteLine($"  Ingest:              {qdIngestMs,8} ms  ({count / (qdIngestMs / 1000.0):F0} vec/s)");
            Console.WriteLine($"  HNSW Search (avg):   {qdHnswMs:F2} ms");
            Console.WriteLine($"  Exact Search (avg):  {qdExactMs:F2} ms");

            try { await _qdrant.DeleteCollectionAsync(qdrantCollection); } catch { }

            double ingestRatio = (double)wlIngestStandardMs / qdIngestMs;
            double hnswRatio = (double)qdHnswMs / wlHnswMs;
            double exactRatio = (double)qdExactMs / wlExactMs;

            Console.WriteLine($"  --- Vergleich ---");
            Console.WriteLine($"  Ingest:  Walhalla {(ingestRatio < 1 ? "SCHNELLER" : "langsamer")} ({ingestRatio:F2}x)");
            Console.WriteLine($"  HNSW:    Walhalla {(hnswRatio > 1 ? "SCHNELLER" : "langsamer")} ({hnswRatio:F2}x)");
            Console.WriteLine($"  Exact:   Walhalla {(exactRatio > 1 ? "SCHNELLER" : "langsamer")} ({exactRatio:F2}x)");

            _perfResults.Add((label, "Walhalla Ingest", $"{wlIngestRate:F0} vec/s"));
            _perfResults.Add((label, "Qdrant Ingest", $"{count / (qdIngestMs / 1000.0):F0} vec/s"));
            _perfResults.Add((label, "Walhalla HNSW", $"{wlHnswMs:F2} ms"));
            _perfResults.Add((label, "Qdrant HNSW", $"{qdHnswMs:F2} ms"));
            _perfResults.Add((label, "Walhalla Exact", $"{wlExactMs:F2} ms"));
            _perfResults.Add((label, "Qdrant Exact", $"{qdExactMs:F2} ms"));
            _perfResults.Add((label, "Walhalla Recall@10", $"{wlRecall:P1}"));
            _perfResults.Add((label, "Disk (MB)", $"{wlDiskSize / 1024.0 / 1024.0:F1}"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  QDRANT FEHLER: {ex.Message}");
        }
    }

    // ========================================================================
    // SECTION 5: HNSW Recall Quality vs ef
    // ========================================================================
    private async Task Section5_RecallVsEf()
    {
        Console.WriteLine("\n========================================");
        Console.WriteLine("SECTION 5: HNSW RECALL vs EF (10.000 Vektoren)");
        Console.WriteLine("  Verschiedene ef-Werte testen");
        Console.WriteLine("========================================\n");

        var (vectors, queryVec, _) = GenerateData(10_000, 42);
        var path = Path.Combine(Path.GetTempPath(), $"wl_stress_r5_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        var store = new BlobStore(new BlobStoreOptions(path));
        var manager = new VectorCollectionManager(store);
        var coll = manager.GetOrCreateCollection("r5", Dimension, DistanceMetric.Cosine,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, EfSearch = 128, AutoScale = false });

        var batch = vectors.Select((v, i) => ((ulong)i, v, (VectorMetadata?)null)).ToList();
        await coll.PutBatchAsync(batch);

        var exactResults = await coll.SearchExactAsync(queryVec, topK: 100).ToListAsync();
        var exactSet = new HashSet<ulong>(exactResults.Select(r => r.Id));
        var exactTop1 = exactResults[0].Id;
        var exactTop10 = new HashSet<ulong>(exactResults.Take(10).Select(r => r.Id));

        Console.WriteLine($"  {"ef",-6} {"Recall@1",-12} {"Recall@10",-12} {"Recall@100",-12} {"Latency (ms)",-14}");
        Console.WriteLine($"  {"----",-6} {"--------",-12} {"---------",-12} {"----------",-12} {"-----------",-14}");

        int[] efValues = [16, 32, 64, 128, 256, 512];

        foreach (var ef in efValues)
        {
            var hnsw1 = await coll.SearchHnswAsync(queryVec, topK: 1, ef: ef).ToListAsync();
            var hnsw10 = await coll.SearchHnswAsync(queryVec, topK: 10, ef: ef).ToListAsync();
            var hnsw100 = await coll.SearchHnswAsync(queryVec, topK: 100, ef: ef).ToListAsync();

            double recall1 = hnsw1.Count > 0 && hnsw1[0].Id == exactTop1 ? 1.0 : 0.0;
            double recall10 = (double)exactTop10.Intersect(hnsw10.Select(r => r.Id)).Count() / 10;
            double recall100 = (double)exactSet.Intersect(hnsw100.Select(r => r.Id)).Count() / 100;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++)
                await coll.SearchHnswAsync(queryVec, topK: 10, ef: ef).ToListAsync();
            sw.Stop();
            double avgMs = sw.ElapsedMilliseconds / 50.0;

            Console.WriteLine($"  {ef,-6} {recall1,-12:P0} {recall10,-12:P1} {recall100,-12:P1} {avgMs,-14:F2}");

            _recallResults.Add(($"ef={ef}", "Recall@1", recall1));
            _recallResults.Add(($"ef={ef}", "Recall@10", recall10));
            _recallResults.Add(($"ef={ef}", "Recall@100", recall100));
            _recallResults.Add(($"ef={ef}", "Latency ms", avgMs));
        }

        store.Dispose();
        Directory.Delete(path, recursive: true);
    }

    // ========================================================================
    // SECTION 6: Concurrent Read/Write Stress
    // ========================================================================
    private async Task Section6_ConcurrentReadWrite()
    {
        Console.WriteLine("\n========================================");
        Console.WriteLine("SECTION 6: CONCURRENT READ/WRITE STRESS");
        Console.WriteLine($"  {ConcurrentReaders} Reader + 1 Writer parallel, {ConcurrentIterations} Iterationen");
        Console.WriteLine("========================================\n");

        var (vectors, queryVec, _) = GenerateData(1_000, 42);
        var path = Path.Combine(Path.GetTempPath(), $"wl_stress_r6_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        var store = new BlobStore(new BlobStoreOptions(path));
        var manager = new VectorCollectionManager(store);
        var coll = manager.GetOrCreateCollection("r6", Dimension, DistanceMetric.Cosine,
            enableHnsw: true, hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, AutoScale = false });

        var batch = vectors.Select((v, i) => ((ulong)i, v, (VectorMetadata?)null)).ToList();
        await coll.PutBatchAsync(batch);

        var errors = new ConcurrentBag<string>();
        var readCounts = new ConcurrentBag<long>();
        var readLatencies = new ConcurrentBag<double>();
        var cts = new CancellationTokenSource();

        // Writer task: continuously update random vectors
        var writerTask = Task.Run(async () =>
        {
            var rng = new Random(99);
            for (int i = 0; i < ConcurrentIterations && !cts.IsCancellationRequested; i++)
            {
                try
                {
                    var id = (ulong)rng.Next(1_000);
                    var newVec = RandomVector(rng, Dimension);
                    await coll.PutAsync(id, newVec);
                }
                catch (Exception ex)
                {
                    errors.Add($"Writer[{i}]: {ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        // Reader tasks
        var readerTasks = new List<Task>();
        for (int r = 0; r < ConcurrentReaders; r++)
        {
            int readerId = r;
            readerTasks.Add(Task.Run(async () =>
            {
                var rng = new Random(200 + readerId);
                var localReads = 0L;
                for (int i = 0; i < ConcurrentIterations && !cts.IsCancellationRequested; i++)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var results = await coll.SearchHnswAsync(queryVec, topK: 10).ToListAsync();
                        sw.Stop();
                        readLatencies.Add(sw.Elapsed.TotalMilliseconds);
                        Interlocked.Increment(ref localReads);
                        await Task.Delay(rng.Next(1, 5));
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Reader[{readerId}]:{i}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                readCounts.Add(localReads);
            }));
        }

        await writerTask;
        await Task.WhenAll(readerTasks);
        cts.Cancel();

        long totalReads = readCounts.Sum();
        bool noErrors = errors.IsEmpty;
        var latencies = readLatencies.ToList();

        Console.WriteLine($"  Total Reads:  {totalReads}");
        Console.WriteLine($"  Total Writes: {ConcurrentIterations}");
        Console.WriteLine($"  Errors:       {errors.Count}");
        if (errors.Count > 0)
        {
            foreach (var e in errors.Take(5))
                Console.WriteLine($"    - {e}");
        }
        if (latencies.Count > 0)
            Console.WriteLine($"  Read Latency: min={latencies.Min():F2}ms  avg={latencies.Average():F2}ms  max={latencies.Max():F2}ms");

        var finalCount = await coll.CountAsync();
        var entry = await coll.GetAsync(500);
        Console.WriteLine($"  Final Count:  {finalCount}");
        Console.WriteLine($"  Get(500):     {(entry is not null ? $"OK ({entry.Vector.Dimension} dim)" : "FEHLT!")}");

        store.Dispose();
        Directory.Delete(path, recursive: true);

        _results.Add(("Concurrent: No Errors", noErrors ? "PASS" : $"FAIL ({errors.Count} errors)"));
        _results.Add(("Concurrent: Count Stable", finalCount == 1_000 ? "PASS" : $"FAIL ({finalCount})"));
        _results.Add(("Concurrent: Data Intact", entry is not null ? "PASS" : "FAIL"));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private (List<Vector> vectors, Vector queryVec, float[] queryFloat) GenerateData(int count, int seed)
    {
        var random = new Random(seed);
        var vectors = new List<Vector>(count);

        for (int i = 0; i < count; i++)
            vectors.Add(RandomVector(random, Dimension));

        var queryFloat = new float[Dimension];
        for (int j = 0; j < Dimension; j++)
            queryFloat[j] = (float)random.NextDouble();
        VectorDistance.NormalizeL2(queryFloat.AsSpan());
        var queryVec = new Vector(queryFloat);

        return (vectors, queryVec, queryFloat);
    }

    private static Vector RandomVector(Random rng, int dim)
    {
        var floats = new float[dim];
        for (int j = 0; j < dim; j++)
            floats[j] = (float)rng.NextDouble();
        VectorDistance.NormalizeL2(floats.AsSpan());
        return new Vector(floats);
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f =>
        {
            try { return new FileInfo(f).Length; } catch { return 0; }
        });
    }

    private static double StdDev(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return 0;
        double avg = list.Average();
        return Math.Sqrt(list.Select(v => Math.Pow(v - avg, 2)).Average());
    }

    // ========================================================================
    // Results tracking
    // ========================================================================

    private readonly List<(string test, string result)> _results = new();
    private readonly List<(string scale, string metric, object value)> _perfResults = new();
    private readonly List<(string config, string metric, object value)> _recallResults = new();

    private void PrintHeader()
    {
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("   WALHALLA vs QDRANT - COMPREHENSIVE STRESS BENCHMARK");
        Console.WriteLine("   Zuverlaessigkeit & Performance");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"   Dimension: {Dimension} (Cosine-normalisiert)");
        Console.WriteLine("   Skalen:    1K, 10K, 50K Vektoren");
        Console.WriteLine("   HNSW:      M=16, EfConstruction=200 (Walhalla & Qdrant)");
        Console.WriteLine("   Qdrant:    localhost:6334");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"Start: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Machine: {Environment.MachineName}, {Environment.ProcessorCount} cores");
        Console.WriteLine();
    }

    private void PrintSummary()
    {
        Console.WriteLine("\n\n========================================================================");
        Console.WriteLine("ZUSAMMENFASSUNG: ZUVERLAeSSIGKEIT");
        Console.WriteLine("========================================================================");
        foreach (var (test, result) in _results)
        {
            var color = result.StartsWith("PASS") ? "  OK  " : "  !!  ";
            Console.WriteLine($"  [{color}] {test}: {result}");
        }

        Console.WriteLine("\n========================================================================");
        Console.WriteLine("ZUSAMMENFASSUNG: RECALL vs EF");
        Console.WriteLine("========================================================================");
        Console.WriteLine($"  {"Config",-8} {"Metric",-22} {"Value",-15}");
        Console.WriteLine($"  {"------",-8} {"------",-22} {"-----",-15}");
        foreach (var (config, metric, value) in _recallResults)
        {
            string valStr = value switch
            {
                double d when d <= 1.0 => $"{d:P1}",
                double d => $"{d:F2}",
                _ => value.ToString() ?? ""
            };
            Console.WriteLine($"  {config,-8} {metric,-22} {valStr,-15}");
        }

        Console.WriteLine("\n========================================================================");
        Console.WriteLine("ZUSAMMENFASSUNG: PERFORMANCE");
        Console.WriteLine("========================================================================");
        Console.WriteLine($"  {"Scale",-8} {"Metric",-22} {"Value",-15}");
        Console.WriteLine($"  {"-----",-8} {"------",-22} {"-----",-15}");
        foreach (var (scale, metric, value) in _perfResults)
        {
            Console.WriteLine($"  {scale,-8} {metric,-22} {value,-15}");
        }
    }
}
