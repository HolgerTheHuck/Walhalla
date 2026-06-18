// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Indexes;

// ═══════════════════════════════════════════════════════════════════════════════
// Real-World Benchmark: Fashion-MNIST (70k Bilder, 784 Dim)
// Vergleicht Walhalla.VectorStore vs Qdrant auf echten Bildvektoren.
// ═══════════════════════════════════════════════════════════════════════════════

const int Dimension = 784;
const int TopK = 10;
const int QueryCount = 100;
const int TrainCount = 10_000; // Subsample für schnelleren IVF-Build

var qdrantHost = Environment.GetEnvironmentVariable("QDRANT_HOST") ?? "localhost";
var qdrantPort = int.TryParse(Environment.GetEnvironmentVariable("QDRANT_PORT"), out var qp) ? qp : 6334;

string[] classNames = { "T-shirt/top", "Trouser", "Pullover", "Dress", "Coat", "Sandal", "Shirt", "Sneaker", "Bag", "Ankle boot" };

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  Fashion-MNIST Benchmark: Walhalla vs Qdrant");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"Dimension: {Dimension}, Train: 60.000, Test Queries: {QueryCount}, TopK: {TopK}");
Console.WriteLine();

// ── Fashion-MNIST laden ─────────────────────────────────────────────────────
var dataDir = Path.Combine(Path.GetTempPath(), "fashion_mnist");
Directory.CreateDirectory(dataDir);

Console.WriteLine("[1] Lade Fashion-MNIST...");

var trainImages = await LoadImagesAsync(
    "http://fashion-mnist.s3-website.eu-central-1.amazonaws.com/train-images-idx3-ubyte.gz",
    "train-images-idx3-ubyte.gz", 60_000, dataDir);

var trainLabels = await LoadLabelsAsync(
    "http://fashion-mnist.s3-website.eu-central-1.amazonaws.com/train-labels-idx1-ubyte.gz",
    "train-labels-idx1-ubyte.gz", 60_000, dataDir);

var testImages = await LoadImagesAsync(
    "http://fashion-mnist.s3-website.eu-central-1.amazonaws.com/t10k-images-idx3-ubyte.gz",
    "t10k-images-idx3-ubyte.gz", 10_000, dataDir);

var testLabels = await LoadLabelsAsync(
    "http://fashion-mnist.s3-website.eu-central-1.amazonaws.com/t10k-labels-idx1-ubyte.gz",
    "t10k-labels-idx1-ubyte.gz", 10_000, dataDir);

// Subsample für schnelleren Vergleich
trainImages = trainImages.Take(TrainCount).ToArray();
trainLabels = trainLabels.Take(TrainCount).ToArray();
Console.WriteLine($"    Train: {trainImages.Length} Bilder (subsampled), Test: {testImages.Length} Bilder geladen.");

// ── Query-Auswahl ───────────────────────────────────────────────────────────
var rnd = new Random(42);
var queryIndices = Enumerable.Range(0, 10_000).OrderBy(_ => rnd.Next()).Take(QueryCount).ToArray();
var queryImages = queryIndices.Select(i => testImages[i]).ToArray();
var queryLabels = queryIndices.Select(i => testLabels[i]).ToArray();

// ── Walhalla initialisieren ─────────────────────────────────────────────────
var walhallaPath = Path.Combine(Path.GetTempPath(), $"walhalla_fmnist_{Guid.NewGuid():N}");
Directory.CreateDirectory(walhallaPath);
using var walhallaStore = new EmbeddedVectorStore(walhallaPath);
var walhallaCollection = walhallaStore.GetOrCreateCollection(
    "fmnist", Dimension, DistanceMetric.Euclidean, enableHnsw: true,
    hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, EfSearch = 128 },
    enableIvf: true,
    ivfOptions: new IvfOptions { NClusters = 100, Nprobe = 3, MaxIterations = 5, RandomSeed = 42 });

// ── Qdrant initialisieren ─────────────────────────────────────────────────
var qdrantClient = new QdrantClient(qdrantHost, qdrantPort, https: false);
var qdrantCollectionName = $"fmnist_{Guid.NewGuid():N}";
try { await qdrantClient.DeleteCollectionAsync(qdrantCollectionName); } catch { }
await qdrantClient.CreateCollectionAsync(
    qdrantCollectionName,
    new VectorParams { Size = (ulong)Dimension, Distance = Distance.Euclid },
    hnswConfig: new HnswConfigDiff { M = 16, EfConstruct = 200 });

// ── Daten einfügen ─────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[2] Füge Trainingsdaten ein...");

var qdrantPoints = new List<PointStruct>();
var sw = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < trainImages.Length; i++)
{
    var id = (ulong)(i + 1);
    await walhallaCollection.UpsertAsync(id, new Walhalla.VectorStore.Vector(trainImages[i]), new Dictionary<string, object>
    {
        ["label"] = trainLabels[i],
        ["class"] = classNames[trainLabels[i]]
    });
    qdrantPoints.Add(new PointStruct
    {
        Id = id,
        Vectors = trainImages[i],
        Payload = { ["label"] = trainLabels[i], ["class"] = classNames[trainLabels[i]] }
    });

    if ((i + 1) % 5000 == 0 || i == trainImages.Length - 1)
        Console.Write($"\r    Eingefügt: {i + 1:N0}/{trainImages.Length:N0}");
}
await walhallaStore.CheckpointAsync();
var wBuildMs = sw.ElapsedMilliseconds;

sw.Restart();
await qdrantClient.UpsertAsync(qdrantCollectionName, qdrantPoints);
var qBuildMs = sw.ElapsedMilliseconds;
Console.WriteLine($"\n    Walhalla Build: {wBuildMs:N0}ms, Qdrant Build: {qBuildMs:N0}ms");

// IVF-Index bauen
sw.Restart();
await walhallaCollection.RebuildIndexAsync(null);
var ivfBuildMs = sw.ElapsedMilliseconds;
Console.WriteLine($"    IVF-Index Build: {ivfBuildMs:N0}ms");

// ── Suchvergleich ──────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[3] Suche und Vergleiche...");
Console.WriteLine();
Console.WriteLine($"{"Query",-5} {"W-HNSW",-8} {"W-IVF-1",-8} {"W-IVF-3",-8} {"W-IVF-5",-8} {"W-IVF-10",-9} {"Q-HNSW",-8} {"W-Q-Üb.",-8}");
Console.WriteLine(new string('═', 75));

var allWRecall = new List<double>();
var allIvf1Recall = new List<double>();
var allIvf3Recall = new List<double>();
var allIvf5Recall = new List<double>();
var allIvf10Recall = new List<double>();
var allQRecall = new List<double>();
var allOverlap = new List<double>();

var ivfSearchMs = new Dictionary<int, List<long>> { [1] = new(), [3] = new(), [5] = new(), [10] = new() };
var wHnswSearchMs = new List<long>();
var qHnswSearchMs = new List<long>();

for (int qi = 0; qi < QueryCount; qi++)
{
    var queryVec = new Walhalla.VectorStore.Vector(queryImages[qi]);
    var queryLabel = queryLabels[qi];

    var wExact = await walhallaCollection.SearchExactAsync(queryVec, TopK).ToListAsync();

    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    var wHnsw  = await walhallaCollection.SearchHnswAsync(queryVec, TopK, ef: 128).ToListAsync();
    wHnswSearchMs.Add(sw2.ElapsedMilliseconds);

    sw2.Restart();
    var qExact = await qdrantClient.SearchAsync(qdrantCollectionName, queryImages[qi], limit: (ulong)TopK, searchParams: new SearchParams { Exact = true });
    var qHnsw  = await qdrantClient.SearchAsync(qdrantCollectionName, queryImages[qi], limit: (ulong)TopK, searchParams: new SearchParams { HnswEf = 128 });
    qHnswSearchMs.Add(sw2.ElapsedMilliseconds);

    var wHnswSet = new HashSet<ulong>(wHnsw.Select(r => r.Id));
    var wExactSet = new HashSet<ulong>(wExact.Select(r => r.Id));
    var qExactSet = new HashSet<ulong>(qExact.Select(r => r.Id.Num));
    var qHnswSet = new HashSet<ulong>(qHnsw.Select(r => r.Id.Num));

    var wRecall = (double)wExactSet.Intersect(wHnswSet).Count() / wExactSet.Count;
    var qRecall = (double)qExactSet.Intersect(qHnswSet).Count() / qExactSet.Count;
    var overlap = (double)wHnswSet.Intersect(qHnswSet).Count() / TopK;

    allWRecall.Add(wRecall);
    allQRecall.Add(qRecall);
    allOverlap.Add(overlap);

    // IVF mit verschiedenen nprobe-Werten
    foreach (var np in new[] { 1, 3, 5, 10 })
    {
        sw2.Restart();
        var ivfResults = await walhallaCollection.SearchIvfAsync(queryVec, TopK, nprobe: np).ToListAsync();
        ivfSearchMs.TryAdd(np, new List<long>());
        ivfSearchMs[np].Add(sw2.ElapsedMilliseconds);

        var ivfSet = new HashSet<ulong>(ivfResults.Select(r => r.Id));
        var ivfRecall = wExactSet.Count > 0 ? (double)wExactSet.Intersect(ivfSet).Count() / wExactSet.Count : 0;

        switch (np)
        {
            case 1: allIvf1Recall.Add(ivfRecall); break;
            case 3: allIvf3Recall.Add(ivfRecall); break;
            case 5: allIvf5Recall.Add(ivfRecall); break;
            case 10: allIvf10Recall.Add(ivfRecall); break;
        }
    }

    if ((qi + 1) % 20 == 0 || qi == QueryCount - 1)
    {
        Console.WriteLine($"{qi + 1,-5} {wRecall,-8:P1} {allIvf1Recall.Last(),-8:P1} {allIvf3Recall.Last(),-8:P1} {allIvf5Recall.Last(),-8:P1} {allIvf10Recall.Last(),-9:P1} {qRecall,-8:P1} {overlap,-8:P1}");
    }
}

// ── Klassenweise Accuracy ─────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[4] Klassenweise Accuracy (W-HNSW vs Ground-Truth)...");
for (int cls = 0; cls < 10; cls++)
{
    var indices = queryIndices.Select((idx, i) => (idx, i)).Where(x => testLabels[x.idx] == cls).Select(x => x.i).ToList();
    if (indices.Count == 0) continue;
    var accs = indices.Select(i =>
    {
        var queryVec = new Walhalla.VectorStore.Vector(queryImages[i]);
        var wHnsw = walhallaCollection.SearchHnswAsync(queryVec, TopK, ef: 128).ToBlockingEnumerable().ToList();
        var correct = wHnsw.Count(r => trainLabels[r.Id - 1] == cls);
        return (double)correct / TopK;
    }).ToList();
    Console.WriteLine($"    {classNames[cls],-15} Queries: {indices.Count,3}  Accuracy@10: {accs.Average():P1}");
}

// ── Zusammenfassung ───────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  Zusammenfassung");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"  Walhalla HNSW Recall@10 (Durchschnitt): {allWRecall.Average():P1} (min {allWRecall.Min():P1}, max {allWRecall.Max():P1})");
Console.WriteLine($"  Walhalla IVF-1 Recall@10 (Avg):        {allIvf1Recall.Average():P1} (min {allIvf1Recall.Min():P1}, max {allIvf1Recall.Max():P1})");
Console.WriteLine($"  Walhalla IVF-3 Recall@10 (Avg):        {allIvf3Recall.Average():P1} (min {allIvf3Recall.Min():P1}, max {allIvf3Recall.Max():P1})");
Console.WriteLine($"  Walhalla IVF-5 Recall@10 (Avg):        {allIvf5Recall.Average():P1} (min {allIvf5Recall.Min():P1}, max {allIvf5Recall.Max():P1})");
Console.WriteLine($"  Walhalla IVF-10 Recall@10 (Avg):       {allIvf10Recall.Average():P1} (min {allIvf10Recall.Min():P1}, max {allIvf10Recall.Max():P1})");
Console.WriteLine($"  Qdrant   HNSW Recall@10 (Durchschnitt): {allQRecall.Average():P1} (min {allQRecall.Min():P1}, max {allQRecall.Max():P1})");
Console.WriteLine($"  Übereinstimmung W-HNSW / Q-HNSW (Avg):  {allOverlap.Average():P1}");
Console.WriteLine();
Console.WriteLine($"  Walhalla HNSW Search/Query (Avg):      {wHnswSearchMs.Average():F1}ms");
Console.WriteLine($"  Walhalla IVF-1 Search/Query (Avg):      {ivfSearchMs[1].Average():F1}ms");
Console.WriteLine($"  Walhalla IVF-3 Search/Query (Avg):      {ivfSearchMs[3].Average():F1}ms");
Console.WriteLine($"  Walhalla IVF-5 Search/Query (Avg):      {ivfSearchMs[5].Average():F1}ms");
Console.WriteLine($"  Walhalla IVF-10 Search/Query (Avg):     {ivfSearchMs[10].Average():F1}ms");
Console.WriteLine($"  Qdrant   HNSW Search/Query (Avg):      {qHnswSearchMs.Average():F1}ms");
Console.WriteLine();
Console.WriteLine($"  Walhalla HNSW Build: {wBuildMs:N0}ms, IVF Build: {ivfBuildMs:N0}ms, Qdrant Build: {qBuildMs:N0}ms");
Console.WriteLine();

// ── Cleanup ───────────────────────────────────────────────────────────────
try { await qdrantClient.DeleteCollectionAsync(qdrantCollectionName); } catch { }
walhallaStore.Dispose();
Directory.Delete(walhallaPath, recursive: true);

Console.WriteLine("Done.");

// ═══════════════════════════════════════════════════════════════════════════════
// Hilfsmethoden
// ═══════════════════════════════════════════════════════════════════════════════

static async Task<float[][]> LoadImagesAsync(string url, string filename, int expectedCount, string dataDir)
{
    var path = Path.Combine(dataDir, filename);
    if (!File.Exists(path))
    {
        Console.WriteLine($"    Download {filename}...");
        using var client = new HttpClient();
        var data = await client.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(path, data);
    }

    using var fs = File.OpenRead(path);
    using var gz = new GZipStream(fs, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    await gz.CopyToAsync(ms);
    var bytes = ms.ToArray();

    // IDX3-ubyte Format: Magic (4), Count (4), Rows (4), Cols (4)
    int count = ReadInt32BigEndian(bytes, 4);
    int rows  = ReadInt32BigEndian(bytes, 8);
    int cols  = ReadInt32BigEndian(bytes, 12);
    int offset = 16;

    if (count != expectedCount || rows != 28 || cols != 28)
        throw new InvalidOperationException($"Unerwartetes Format: count={count}, rows={rows}, cols={cols}");

    var images = new float[count][];
    for (int i = 0; i < count; i++)
    {
        images[i] = new float[rows * cols];
        for (int j = 0; j < rows * cols; j++)
            images[i][j] = bytes[offset + i * rows * cols + j] / 255.0f;
    }
    return images;
}

static async Task<int[]> LoadLabelsAsync(string url, string filename, int expectedCount, string dataDir)
{
    var path = Path.Combine(dataDir, filename);
    if (!File.Exists(path))
    {
        Console.WriteLine($"    Download {filename}...");
        using var client = new HttpClient();
        var data = await client.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(path, data);
    }

    using var fs = File.OpenRead(path);
    using var gz = new GZipStream(fs, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    await gz.CopyToAsync(ms);
    var bytes = ms.ToArray();

    int count = ReadInt32BigEndian(bytes, 4);
    int offset = 8;

    if (count != expectedCount)
        throw new InvalidOperationException($"Unerwartetes Label-Format: count={count}");

    var labels = new int[count];
    for (int i = 0; i < count; i++)
        labels[i] = bytes[offset + i];
    return labels;
}

static int ReadInt32BigEndian(byte[] data, int offset)
{
    return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
