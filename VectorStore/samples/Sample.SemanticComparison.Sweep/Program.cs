// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Indexes;

// ═══════════════════════════════════════════════════════════════════════════════
// HNSW-Parameter-Sweep – Walhalla vs Qdrant mit Zufallsvektoren
// Testet verschiedene HNSW-Konfigurationen bei 10k Dokumenten.
// ═══════════════════════════════════════════════════════════════════════════════

const int Dimension = 768;
const int Count = 10_000;
const int TopK = 10;
const int QueryCount = 20;

var qdrantHost = Environment.GetEnvironmentVariable("QDRANT_HOST") ?? "localhost";
var qdrantPort = int.TryParse(Environment.GetEnvironmentVariable("QDRANT_PORT"), out var qp) ? qp : 6334;

var configs = new[]
{
    (M: 16,  EfConstruction: 200, EfSearch: 64,  Label: "M16_EF200_EF64"),
    (M: 16,  EfConstruction: 200, EfSearch: 128, Label: "M16_EF200_EF128"),
    (M: 16,  EfConstruction: 200, EfSearch: 256, Label: "M16_EF200_EF256"),
    (M: 32,  EfConstruction: 200, EfSearch: 64,  Label: "M32_EF200_EF64"),
    (M: 32,  EfConstruction: 200, EfSearch: 128, Label: "M32_EF200_EF128"),
    (M: 32,  EfConstruction: 400, EfSearch: 128, Label: "M32_EF400_EF128"),
    (M: 32,  EfConstruction: 400, EfSearch: 256, Label: "M32_EF400_EF256"),
    (M: 64,  EfConstruction: 400, EfSearch: 128, Label: "M64_EF400_EF128"),
    (M: 64,  EfConstruction: 400, EfSearch: 256, Label: "M64_EF400_EF256"),
    (M: 64,  EfConstruction: 400, EfSearch: 512, Label: "M64_EF400_EF512"),
};

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  HNSW Parameter-Sweep: Walhalla vs Qdrant");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"Dimension: {Dimension}, Count: {Count:N0}, Queries: {QueryCount}, TopK: {TopK}");
Console.WriteLine();

// ── Zufallsvektoren erzeugen ────────────────────────────────────────────────
Console.WriteLine("[1] Erzeuge Zufallsvektoren...");
var rnd = new Random(42);
var vectors = new List<float[]>();
for (int i = 0; i < Count; i++)
{
    var v = new float[Dimension];
    for (int j = 0; j < Dimension; j++) v[j] = (float)rnd.NextDouble();
    VectorDistance.NormalizeL2(v.AsSpan());
    vectors.Add(v);
}

var queries = new List<float[]>();
for (int i = 0; i < QueryCount; i++)
{
    var v = new float[Dimension];
    for (int j = 0; j < Dimension; j++) v[j] = (float)rnd.NextDouble();
    VectorDistance.NormalizeL2(v.AsSpan());
    queries.Add(v);
}
Console.WriteLine($"    {Count:N0} Vektoren + {QueryCount} Queries erzeugt.");

// ── Qdrant Client ───────────────────────────────────────────────────────────
var qdrantClient = new QdrantClient(qdrantHost, qdrantPort, https: false);

// ── Für jede Konfiguration testen ───────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[2] Teste Konfigurationen...");
Console.WriteLine();
Console.WriteLine($"{"Konfiguration",-22} {"W-Recall",-10} {"Q-Recall",-10} {"W-Q-Üb.",-10} {"W-Q-RankDiff",-14} {"W-BuildMs",-12} {"Q-BuildMs",-12}");
Console.WriteLine(new string('═', 105));

foreach (var cfg in configs)
{
    var walhallaPath = Path.Combine(Path.GetTempPath(), $"walhalla_sweep_{Guid.NewGuid():N}");
    Directory.CreateDirectory(walhallaPath);
    using var walhallaStore = new EmbeddedVectorStore(walhallaPath);
    var walhallaCollection = walhallaStore.GetOrCreateCollection(
        "test", Dimension, DistanceMetric.Cosine, enableHnsw: true,
        hnswOptions: new HnswOptions { M = cfg.M, EfConstruction = cfg.EfConstruction });

    var qdrantCollectionName = $"sweep_{cfg.Label}_{Guid.NewGuid():N}";
    try { await qdrantClient.DeleteCollectionAsync(qdrantCollectionName); } catch { }
    await qdrantClient.CreateCollectionAsync(
        qdrantCollectionName,
        new VectorParams { Size = (ulong)Dimension, Distance = Distance.Cosine },
        hnswConfig: new HnswConfigDiff { M = (ulong)cfg.M, EfConstruct = (ulong)cfg.EfConstruction });

    // Daten einfügen
    var qdrantPoints = new List<PointStruct>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < Count; i++)
    {
        var id = (ulong)(i + 1);
        await walhallaCollection.UpsertAsync(id, new Walhalla.VectorStore.Vector(vectors[i]));
        qdrantPoints.Add(new PointStruct { Id = id, Vectors = vectors[i] });
    }
    await walhallaStore.CheckpointAsync();
    var wBuildMs = sw.ElapsedMilliseconds;

    sw.Restart();
    await qdrantClient.UpsertAsync(qdrantCollectionName, qdrantPoints);
    var qBuildMs = sw.ElapsedMilliseconds;

    // Suchen
    var wRecalls = new List<double>();
    var qRecalls = new List<double>();
    var overlaps = new List<double>();
    var rankDiffs = new List<double>();

    foreach (var query in queries)
    {
        var qv = new Walhalla.VectorStore.Vector(query);
        var wExact = await walhallaCollection.SearchExactAsync(qv, TopK).ToListAsync();
        var wHnsw  = await walhallaCollection.SearchHnswAsync(qv, TopK, ef: cfg.EfSearch).ToListAsync();
        var qExact = await qdrantClient.SearchAsync(qdrantCollectionName, query, limit: (ulong)TopK, searchParams: new SearchParams { Exact = true });
        var qHnsw  = await qdrantClient.SearchAsync(qdrantCollectionName, query, limit: (ulong)TopK, searchParams: new SearchParams { HnswEf = (ulong)cfg.EfSearch });

        var wExactSet = new HashSet<ulong>(wExact.Select(r => r.Id));
        var wHnswSet  = new HashSet<ulong>(wHnsw.Select(r => r.Id));
        var qExactSet = new HashSet<ulong>(qExact.Select(r => r.Id.Num));
        var qHnswSet  = new HashSet<ulong>(qHnsw.Select(r => r.Id.Num));

        var wRecall = (double)wExactSet.Intersect(wHnswSet).Count() / wExactSet.Count;
        var qRecall = (double)qExactSet.Intersect(qHnswSet).Count() / qExactSet.Count;
        var overlap = (double)wHnswSet.Intersect(qHnswSet).Count() / TopK;

        // Rank-Diff zwischen W-HNSW und Q-HNSW
        var wHnswRank = wHnsw.Select((r, idx) => (r.Id, rank: idx)).ToDictionary(x => x.Id, x => x.rank);
        var qHnswRank = qHnsw.Select((r, idx) => (r.Id.Num, rank: idx)).ToDictionary(x => x.Num, x => x.rank);
        var common = wHnswRank.Keys.Intersect(qHnswRank.Keys);
        var rankDiff = common.Any() ? common.Average(id => Math.Abs(wHnswRank[id] - qHnswRank[id])) : 0;

        wRecalls.Add(wRecall);
        qRecalls.Add(qRecall);
        overlaps.Add(overlap);
        rankDiffs.Add(rankDiff);
    }

    // Cleanup
    try { await qdrantClient.DeleteCollectionAsync(qdrantCollectionName); } catch { }
    walhallaStore.Dispose();
    Directory.Delete(walhallaPath, recursive: true);

    Console.WriteLine($"{cfg.Label,-22} {wRecalls.Average(),-10:P1} {qRecalls.Average(),-10:P1} {overlaps.Average(),-10:P1} {rankDiffs.Average(),-14:F2} {wBuildMs,-12:N0} {qBuildMs,-12:N0}");
}

Console.WriteLine();
Console.WriteLine("Done.");
