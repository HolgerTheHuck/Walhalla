using System;
using System.IO;
using System.Linq;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;

class TestGraph
{
    static void Main()
    {
        var random = new Random(42);
        int count = 1000;
        int dim = 1536;

        var vectors = new System.Collections.Generic.List<Walhalla.VectorStore.Vector>(count);
        for (int i = 0; i < count; i++)
        {
            var floats = new float[dim];
            for (int j = 0; j < dim; j++)
                floats[j] = (float)random.NextDouble();
            Walhalla.VectorStore.VectorDistance.NormalizeL2(floats.AsSpan());
            vectors.Add(new Walhalla.VectorStore.Vector(floats));
        }

        var path = Path.Combine(Path.GetTempPath(), $"walhalla_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        var store = new BlobStore(new BlobStoreOptions(path));
        var manager = new VectorCollectionManager(store);
        var collection = manager.GetOrCreateCollection("test", dim, DistanceMetric.Cosine, enableHnsw: true,
            hnswOptions: new HnswOptions { M = 16, EfConstruction = 200 });

        var batch = new System.Collections.Generic.List<(ulong Id, Walhalla.VectorStore.Vector Vector, VectorMetadata? Metadata)>();
        for (int i = 0; i < count; i++)
            batch.Add(((ulong)i, vectors[i], null));
        collection.PutBatchAsync(batch).Wait();
        store.CheckpointAsync().Wait();
        collection.RebuildIndexAsync(null).Wait();

        var index = collection.Index!;
        Console.WriteLine($"NodeCount: {index.NodeCount}");
        Console.WriteLine($"EntryLayer: {index.EntryLayer}");
        Console.WriteLine($"EntryPointIndex: {index.EntryPointIndex}");

        // Graph-Statistiken
        var nodes = index.GetAllNodes().ToList();
        Console.WriteLine($"Total nodes: {nodes.Count}");

        for (int layer = 0; layer <= index.EntryLayer; layer++)
        {
            int nodesInLayer = 0;
            int totalNeighbors = 0;
            int maxNeighbors = 0;
            int minNeighbors = int.MaxValue;
            foreach (var node in nodes)
            {
                if (layer <= node.TopLayer)
                {
                    nodesInLayer++;
                    var neighborCount = node.GetNeighborCount(layer);
                    totalNeighbors += neighborCount;
                    maxNeighbors = Math.Max(maxNeighbors, neighborCount);
                    minNeighbors = Math.Min(minNeighbors, neighborCount);
                }
            }
            Console.WriteLine($"Layer {layer}: {nodesInLayer} nodes, avg neighbors: {(double)totalNeighbors / nodesInLayer:F1}, min: {minNeighbors}, max: {maxNeighbors}");
        }

        // Teste Suche
        var queryFloats = new float[dim];
        for (int j = 0; j < dim; j++)
            queryFloats[j] = (float)random.NextDouble();
        Walhalla.VectorStore.VectorDistance.NormalizeL2(queryFloats.AsSpan());
        var queryVector = new Walhalla.VectorStore.Vector(queryFloats);

        var exactResults = collection.SearchExactAsync(queryVector, topK: 10).ToListAsync().Result;
        var hnswResults = collection.SearchHnswAsync(queryVector, topK: 10, ef: 64).ToListAsync().Result;

        var exactIds = new System.Collections.Generic.HashSet<ulong>(exactResults.Select(r => r.Id));
        var hnswIds = new System.Collections.Generic.HashSet<ulong>(hnswResults.Select(r => r.Id));
        var recall = (double)exactIds.Intersect(hnswIds).Count() / exactIds.Count;
        Console.WriteLine($"Recall@10: {recall:P1}");
        Console.WriteLine($"Exact results: {string.Join(", ", exactResults.Select(r => r.Id))}");
        Console.WriteLine($"HNSW results: {string.Join(", ", hnswResults.Select(r => r.Id))}");

        store.Dispose();
        Directory.Delete(path, recursive: true);
    }
}
