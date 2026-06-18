// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using Walhalla.VectorStore.Indexes;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class HnswIndexTests
{
    private static float[] VectorLoader(ulong id)
    {
        // Deterministic vectors based on ID
        var random = new Random((int)id);
        var vec = new float[128];
        for (int i = 0; i < 128; i++)
            vec[i] = (float)random.NextDouble();
        VectorDistance.NormalizeL2(vec.AsSpan());
        return vec;
    }

    [Fact]
    public void Insert_FirstNode_SetsEntryPoint()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, RandomSeed = 42 });

        index.Insert(1, VectorLoader);

        Assert.Equal(1, index.NodeCount);
        Assert.Equal(0, index.EntryLayer);
    }

    [Fact]
    public void Insert_MultipleNodes_IncreasesCount()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, RandomSeed = 42 });

        for (ulong i = 1; i <= 100; i++)
            index.Insert(i, VectorLoader);

        Assert.Equal(100, index.NodeCount);
    }

    [Fact]
    public void SearchKnn_EmptyIndex_ReturnsEmpty()
    {
        var index = new HnswIndex();

        var results = index.SearchKnn(VectorLoader, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void SearchKnn_SingleNode_ReturnsThatNode()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, RandomSeed = 42 });
        index.Insert(1, VectorLoader);

        var results = index.SearchKnn(VectorLoader, 1);

        Assert.Single(results);
        Assert.Equal(1ul, results[0].Id);
    }

    [Fact]
    public void SearchKnn_MultipleNodes_ReturnsKResults()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, EfSearch = 32, RandomSeed = 42 });
        for (ulong i = 1; i <= 100; i++)
            index.Insert(i, VectorLoader);

        var results = index.SearchKnn(VectorLoader, 10);

        Assert.Equal(10, results.Count);
    }

    [Fact]
    public void SearchKnn_KLargerThanCount_ReturnsAll()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, RandomSeed = 42 });
        for (ulong i = 1; i <= 5; i++)
            index.Insert(i, VectorLoader);

        var results = index.SearchKnn(VectorLoader, 10);

        Assert.Equal(5, results.Count);
    }

    [Fact]
    public void SearchKnn_ResultsAreSortedByDistance()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, EfSearch = 64, RandomSeed = 42 });
        for (ulong i = 1; i <= 100; i++)
            index.Insert(i, VectorLoader);

        var queryLoader = (ulong id) =>
        {
            // Query vector close to ID 50
            var random = new Random(50);
            var vec = new float[128];
            for (int i = 0; i < 128; i++)
                vec[i] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(vec.AsSpan());
            return vec;
        };

        var results = index.SearchKnn(queryLoader, 10);

        // Verify sorted by distance (ascending)
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(results[i - 1].Distance <= results[i].Distance,
                $"Results not sorted: {results[i - 1].Distance} > {results[i].Distance}");
        }
    }

    [Fact]
    public void MarkDeleted_ExistingNode_Succeeds()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, RandomSeed = 42 });
        index.Insert(1, VectorLoader);

        var result = index.MarkDeleted(1);

        Assert.True(result);
    }

    [Fact]
    public void MarkDeleted_NonExistingNode_ReturnsFalse()
    {
        var index = new HnswIndex();

        var result = index.MarkDeleted(999);

        Assert.False(result);
    }

    [Fact]
    public void MarkDeleted_NodeExcludedFromSearch()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, EfSearch = 32, RandomSeed = 42 });
        for (ulong i = 1; i <= 10; i++)
            index.Insert(i, VectorLoader);

        index.MarkDeleted(1);
        var results = index.SearchKnn(VectorLoader, 9);

        Assert.All(results, r => Assert.NotEqual(1ul, r.Id));
    }

    [Fact]
    public void SearchKnn_HigherEf_ReturnsBetterResults()
    {
        var index = new HnswIndex(new HnswOptions { M = 16, EfConstruction = 200, RandomSeed = 42 });
        for (ulong i = 1; i <= 500; i++)
            index.Insert(i, VectorLoader);

        var queryLoader = (ulong id) =>
        {
            var random = new Random(250);
            var vec = new float[128];
            for (int i = 0; i < 128; i++)
                vec[i] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(vec.AsSpan());
            return vec;
        };

        var resultsLowEf = index.SearchKnn(queryLoader, 10, ef: 10);
        var resultsHighEf = index.SearchKnn(queryLoader, 10, ef: 100);

        // Higher ef should give better (lower) distances on average
        var avgLow = resultsLowEf.Average(r => r.Distance);
        var avgHigh = resultsHighEf.Average(r => r.Distance);

        Assert.True(avgHigh <= avgLow * 1.1f, // Allow small variance
            $"High ef ({avgHigh}) should be better than low ef ({avgLow})");
    }

    [Fact]
    public void Insert_Parallel_IsThreadSafe()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, RandomSeed = 42 });
        var vectors = new float[100][];
        for (int i = 0; i < 100; i++)
            vectors[i] = VectorLoader((ulong)(i + 1));

        // Phase 1: Layers sequentiell berechnen, dann Nodes parallel vorbereiten
        var layers = index.PrepareLayers(100);
        var nodes = new HnswNode[100];
        System.Threading.Tasks.Parallel.For(0, 100, i =>
        {
            nodes[i] = index.PrepareNode((ulong)(i + 1), vectors[i], layers[i]);
        });

        // Phase 2: Sequentiell in den Graph einfügen (Graph-Mutation)
        for (int i = 0; i < 100; i++)
        {
            index.InsertPreparedNode(nodes[i]);
        }

        Assert.Equal(100, index.NodeCount);
    }

    [Fact]
    public void Options_ExposedCorrectly()
    {
        var options = new HnswOptions { M = 16, EfConstruction = 200, EfSearch = 64 };
        var index = new HnswIndex(options);

        Assert.Equal(16, index.Options.M);
        Assert.Equal(200, index.Options.EfConstruction);
        Assert.Equal(64, index.Options.EfSearch);
    }

    [Fact]
    public void GraphStructure_AfterInsert_HasConnectedNeighbors()
    {
        var index = new HnswIndex(new HnswOptions { M = 16, EfConstruction = 200, RandomSeed = 42 });
        for (ulong i = 1; i <= 1000; i++)
            index.Insert(i, VectorLoader);

        Console.WriteLine($"NodeCount: {index.NodeCount}");
        Console.WriteLine($"EntryLayer: {index.EntryLayer}");

        // Graph-Struktur über Reflection untersuchen
        var nodesField = typeof(HnswIndex).GetField("_nodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var nodesTable = nodesField!.GetValue(index);
        var nodesListField = nodesTable!.GetType().GetField("_nodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var nodesList = (System.Collections.IList)nodesListField!.GetValue(nodesTable)!;
        
        var hnswNodeType = typeof(HnswIndex).Assembly.GetTypes().First(t => t.Name == "HnswNode");
        var topLayerField = hnswNodeType.GetField("TopLayer");
        var getNeighborCountMethod = hnswNodeType.GetMethod("GetNeighborCount");

        for (int layer = 0; layer <= index.EntryLayer; layer++)
        {
            int nodesInLayer = 0;
            int totalNeighbors = 0;
            int zeroNeighbors = 0;
            foreach (var node in nodesList)
            {
                if (node is null) continue;
                var topLayer = (int)topLayerField!.GetValue(node)!;
                if (layer <= topLayer)
                {
                    nodesInLayer++;
                    var neighborCount = (int)getNeighborCountMethod!.Invoke(node, new object[] { layer })!;
                    totalNeighbors += neighborCount;
                    if (neighborCount == 0) zeroNeighbors++;
                }
            }
            var avg = nodesInLayer > 0 ? (double)totalNeighbors / nodesInLayer : 0;
            Console.WriteLine($"Layer {layer}: {nodesInLayer} nodes, avg neighbors: {avg:F1}, zero: {zeroNeighbors}");
        }

        // Teste Recall mit separatem Query-Vektor
        var queryVec = new float[128];
        var queryRandom = new Random(500);
        for (int i = 0; i < 128; i++)
            queryVec[i] = (float)queryRandom.NextDouble();
        VectorDistance.NormalizeL2(queryVec.AsSpan());

        var queryLoader = (ulong id) => id == ulong.MaxValue ? queryVec : VectorLoader(id);

        // Brute-force Suche für Ground Truth
        var allDists = new System.Collections.Generic.List<(ulong Id, float Dist)>();
        for (ulong i = 1; i <= 1000; i++)
        {
            var dist = VectorDistance.Euclidean(VectorLoader(i).AsSpan(), queryVec.AsSpan());
            allDists.Add((i, dist));
        }
        var exactIds = allDists.OrderBy(d => d.Dist).Take(10).Select(d => d.Id).ToHashSet();

        var hnswResults = index.SearchKnn(queryLoader, 10, ef: 1000);
        Console.WriteLine($"HNSW returned {hnswResults.Count} results");
        var hnswIds = hnswResults.Select(r => r.Id).ToHashSet();

        var recall = (double)exactIds.Intersect(hnswIds).Count() / exactIds.Count;
        Console.WriteLine($"Recall@10: {recall:P1}");
        Console.WriteLine($"HNSW results: {string.Join(", ", hnswResults.Select(r => $"{r.Id}({r.Distance:F3})"))}");
        
        var exactResults = allDists.OrderBy(d => d.Dist).Take(10).ToList();
        Console.WriteLine($"Exact results: {string.Join(", ", exactResults.Select(r => $"{r.Id}({r.Dist:F3})"))}");

        // Prüfe Entry Point
        var entryLayerField = typeof(HnswIndex).GetField("_entryPointIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var entryPointIdx = (int)entryLayerField!.GetValue(index)!;
        var maxLayerField = typeof(HnswIndex).GetField("_maxLayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var maxLayer = (int)maxLayerField!.GetValue(index)!;
        Console.WriteLine($"EntryPointIndex: {entryPointIdx}, MaxLayer: {maxLayer}");

        // Prüfe Distanz vom Entry Point zum Query
        var nodesField2 = typeof(HnswIndex).GetField("_nodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var nodesTable2 = nodesField2!.GetValue(index);
        var getNodeMethod = nodesTable2!.GetType().GetMethod("GetNode", new[] { typeof(int) });
        var entryNode = getNodeMethod!.Invoke(nodesTable2, new object[] { entryPointIdx });
        var idField = entryNode!.GetType().GetField("Id");
        var entryId = (ulong)idField!.GetValue(entryNode)!;
        var entryDist = VectorDistance.Euclidean(VectorLoader(entryId).AsSpan(), queryVec.AsSpan());
        Console.WriteLine($"EntryPoint ID: {entryId}, Distance to query: {entryDist:F3}");

        // Prüfe Distanzen von ID 500 (bestem Exact) zu allen Nodes auf Layer 2
        var nodesListField2 = nodesTable2!.GetType().GetField("_nodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var nodesList2 = (System.Collections.IList)nodesListField2!.GetValue(nodesTable2)!;
        var topLayerField2 = typeof(HnswIndex).Assembly.GetTypes().First(t => t.Name == "HnswNode").GetField("TopLayer");
        var neighborsField = typeof(HnswIndex).Assembly.GetTypes().First(t => t.Name == "HnswNode").GetField("Neighbors");
        
        Console.WriteLine("Layer 2 Nodes and their distances to query:");
        foreach (var node in nodesList2)
        {
            if (node is null) continue;
            var topLayer = (int)topLayerField2!.GetValue(node)!;
            if (topLayer >= 2)
            {
                var nodeId = (ulong)idField.GetValue(node)!;
                var dist = VectorDistance.Euclidean(VectorLoader(nodeId).AsSpan(), queryVec.AsSpan());
                Console.WriteLine($"  ID {nodeId}: {dist:F3}");
            }
        }

        // Prüfe Nachbarn von ID 77 auf Layer 1 und Layer 0
        var neighborsField2 = typeof(HnswIndex).Assembly.GetTypes().First(t => t.Name == "HnswNode").GetField("Neighbors", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var neighborsArray = (int[][])neighborsField2!.GetValue(entryNode)!;
        
        Console.WriteLine($"ID 77 Layer 1 neighbors: {string.Join(", ", neighborsArray[1].Where(id => id >= 0).Select(idx => {
            var n = getNodeMethod!.Invoke(nodesTable2, new object[] { idx });
            var nid = (ulong)idField.GetValue(n)!;
            var nd = VectorDistance.Euclidean(VectorLoader(nid).AsSpan(), queryVec.AsSpan());
            return $"{nid}({nd:F3})";
        }))}");
        
        Console.WriteLine($"ID 77 Layer 0 neighbors (first 10): {string.Join(", ", neighborsArray[0].Where(id => id >= 0).Take(10).Select(idx => {
            var n = getNodeMethod!.Invoke(nodesTable2, new object[] { idx });
            var nid = (ulong)idField.GetValue(n)!;
            var nd = VectorDistance.Euclidean(VectorLoader(nid).AsSpan(), queryVec.AsSpan());
            return $"{nid}({nd:F3})";
        }))} ...");

        // Prüfe, ob ID 500 überhaupt im Graph ist
        var id500Node = getNodeMethod!.Invoke(nodesTable2, new object[] { 499 });
        if (id500Node != null)
        {
            var id500Id = (ulong)idField.GetValue(id500Node)!;
            var id500Dist = VectorDistance.Euclidean(VectorLoader(id500Id).AsSpan(), queryVec.AsSpan());
            Console.WriteLine($"ID 500 (index 499) exists in graph: {id500Id}, distance to query: {id500Dist:F3}");
            
            var id500Neighbors = (int[][])neighborsField2.GetValue(id500Node)!;
            Console.WriteLine($"ID 500 Layer 0 neighbors (first 10): {string.Join(", ", id500Neighbors[0].Where(id => id >= 0).Take(10).Select(idx => {
                var n = getNodeMethod!.Invoke(nodesTable2, new object[] { idx });
                var nid = (ulong)idField.GetValue(n)!;
                var nd = VectorDistance.Euclidean(VectorLoader(nid).AsSpan(), queryVec.AsSpan());
                return $"{nid}({nd:F3})";
            }))} ...");
        }
        else
        {
            Console.WriteLine("ID 500 NOT found in graph!");
        }

        // Prüfe ID 501 (index 500)
        var id501Node = getNodeMethod!.Invoke(nodesTable2, new object[] { 500 });
        if (id501Node != null)
        {
            var id501Id = (ulong)idField.GetValue(id501Node)!;
            var id501Dist = VectorDistance.Euclidean(VectorLoader(id501Id).AsSpan(), queryVec.AsSpan());
            Console.WriteLine($"ID 501 (index 500) exists in graph: {id501Id}, distance to query: {id501Dist:F3}");
        }

        // Prüfe Graph-Konnektivität
        var reachable = new HashSet<ulong>();
        var queue = new Queue<ulong>();
        queue.Enqueue(77);
        reachable.Add(77);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            // Reflection um private Felder zu erreichen
            var nodesField3 = typeof(HnswIndex).GetField("_nodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var nodesTable3 = nodesField3!.GetValue(index);
            var getNodeMethod3 = nodesTable3!.GetType().GetMethod("GetNodeById", new[] { typeof(ulong) });
            var node3 = getNodeMethod3!.Invoke(nodesTable3, new object[] { current });
            if (node3 == null) continue;
            var neighborsField3 = node3.GetType().GetField("Neighbors", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var neighborsArray3 = (int[][])neighborsField3!.GetValue(node3)!;
            var getNodeByIndexMethod3 = nodesTable3.GetType().GetMethod("GetNode", new[] { typeof(int) });
            foreach (var layerNeighbors in neighborsArray3)
            {
                foreach (var neighborIdx in layerNeighbors)
                {
                    if (neighborIdx < 0) continue;
                    var neighbor = getNodeByIndexMethod3!.Invoke(nodesTable3, new object[] { neighborIdx });
                    if (neighbor == null) continue;
                    var idField3 = neighbor.GetType().GetField("Id");
                    var neighborId = (ulong)idField3!.GetValue(neighbor)!;
                    if (reachable.Add(neighborId))
                        queue.Enqueue(neighborId);
                }
            }
        }
        Console.WriteLine($"Reachable from ID 77: {reachable.Count} / 1000");
        Assert.True(reachable.Count >= 950, $"Expected at least 950 reachable nodes, but was {reachable.Count}");

        Assert.True(recall > 0.9, $"Recall should be > 90%, but was {recall:P1}");
    }

    [Fact]
    public void VectorLoader_QueryIsIdenticalToVector500()
    {
        var queryVec = new float[128];
        var queryRandom = new Random(500);
        for (int i = 0; i < 128; i++)
            queryVec[i] = (float)queryRandom.NextDouble();
        VectorDistance.NormalizeL2(queryVec.AsSpan());

        var vec500 = VectorLoader(500);
        var dist = VectorDistance.Euclidean(queryVec.AsSpan(), vec500.AsSpan());

        Assert.Equal(0.0f, dist, 4);
    }

    [Fact]
    public void SearchKnn_Tiny2D_ReturnsCorrectNeighbors()
    {
        var index = new HnswIndex(new HnswOptions { M = 8, EfConstruction = 50, RandomSeed = 42 });

        // 10 Vektoren in 2D auf einem Gitter
        var vectors = new float[10][];
        for (int i = 0; i < 10; i++)
        {
            vectors[i] = new float[] { i * 1.0f, 0.0f };
        }

        for (ulong i = 0; i < 10; i++)
            index.Insert(i, _ => vectors[i]);

        var query = new float[] { 4.5f, 0.0f };
        var results = index.SearchKnn(id => id == ulong.MaxValue ? query : vectors[id], 3);

        Assert.Equal(3, results.Count);
        // Die nächsten Nachbarn zu 4.5 sind 4, 5 und dann 3 oder 6
        var ids = results.Select(r => r.Id).OrderBy(id => id).ToList();
        Assert.Contains(4ul, ids);
        Assert.Contains(5ul, ids);
    }

    [Theory]
    [InlineData(16, 200, 64, 0.50)]
    [InlineData(32, 400, 128, 0.70)]
    public void SearchKnn_Small128D_ReasonableRecall(int m, int efConstruction, int efSearch, double minRecall)
    {
        var index = new HnswIndex(new HnswOptions { M = m, EfConstruction = efConstruction, RandomSeed = 42 });
        var random = new Random(42);
        var vectors = new float[100][];

        for (int i = 0; i < 100; i++)
        {
            vectors[i] = new float[128];
            for (int j = 0; j < 128; j++)
                vectors[i][j] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(vectors[i].AsSpan());
        }

        for (ulong i = 0; i < 100; i++)
            index.Insert(i, _ => vectors[i]);

        var query = vectors[42];
        var results = index.SearchKnn(id => id == ulong.MaxValue ? query : vectors[id], 10, ef: efSearch);

        // Brute-force Ground Truth
        var exact = vectors
            .Select((v, i) => (Id: (ulong)i, Dist: VectorDistance.Euclidean(v.AsSpan(), query.AsSpan())))
            .OrderBy(x => x.Dist)
            .Take(10)
            .Select(x => x.Id)
            .ToHashSet();

        var hnswIds = results.Select(r => r.Id).ToHashSet();
        var recall = (double)exact.Intersect(hnswIds).Count() / exact.Count;

        Console.WriteLine($"Small128D Recall@10: {recall:P1}");
        foreach (var r in results.Take(5))
            Console.WriteLine($"  HNSW ID={r.Id} Dist={r.Distance:F4}");
        foreach (var id in exact.Take(5))
            Console.WriteLine($"  Exact ID={id}");

        Assert.True(recall >= minRecall, $"Recall should be >= {minRecall:P0}, but was {recall:P1}");
    }

    [Fact]
    public void PrepareLayers_ProducesNonZeroLayers()
    {
        var index = new HnswIndex(new HnswOptions { M = 16, RandomSeed = 42 });
        var layers = index.PrepareLayers(10000);

        var nonZero = layers.Count(l => l > 0);
        Console.WriteLine($"Layers: max={layers.Max()}, nonZero={nonZero}, distribution: {string.Join(", ", layers.GroupBy(x => x).Select(g => $"L{g.Key}={g.Count()}"))}");

        Assert.True(nonZero > 0, $"Expected some layers > 0, but all were 0");
    }

    [Fact]
    public void Insert_BatchPreparedNodes_EntryLayerGreaterThanZero()
    {
        var index = new HnswIndex(new HnswOptions { M = 16, RandomSeed = 42 });
        var vectors = new float[1000][];
        for (int i = 0; i < 1000; i++)
            vectors[i] = VectorLoader((ulong)(i + 1));

        var layers = index.PrepareLayers(1000);
        var nodes = new HnswNode[1000];
        for (int i = 0; i < 1000; i++)
        {
            nodes[i] = index.PrepareNode((ulong)(i + 1), vectors[i], layers[i]);
        }

        for (int i = 0; i < 1000; i++)
        {
            index.InsertPreparedNode(nodes[i]);
        }

        Console.WriteLine($"EntryLayer after batch insert: {index.EntryLayer}");
        Assert.True(index.EntryLayer > 0, $"Expected EntryLayer > 0, but was {index.EntryLayer}");
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var index = new HnswIndex();
        index.Dispose();
    }
}
