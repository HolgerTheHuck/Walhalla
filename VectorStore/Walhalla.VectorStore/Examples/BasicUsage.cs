// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.VectorStore.Examples;

/// <summary>Beispiel für Collections mit gemeinsamem Store.</summary>
public static class BasicUsage
{
    public static async Task RunAsync()
    {
        var options = new StorageEngineOptions { RootPath = "vector_db" };
        using var store = new EmbeddedVectorStore(options);

        // Manager wird intern vom EmbeddedVectorStore verwaltet

        // Collection 1: Dokument-Embeddings
        var docs = store.GetOrCreateCollection(
            "documents",
            dimension: 1536,
            metric: DistanceMetric.Cosine,
            enableHnsw: true,
            hnswOptions: new HnswOptions { M = 16, EfConstruction = 200 });

        // Collection 2: Bild-Embeddings
        var images = store.GetOrCreateCollection(
            "images",
            dimension: 512,
            metric: DistanceMetric.Euclidean,
            enableHnsw: true);

        // Vektoren einfügen
        var random = new Random();
        for (ulong i = 0; i < 1000; i++)
        {
            var floats = new float[1536];
            for (int j = 0; j < floats.Length; j++)
                floats[j] = (float)random.NextDouble();

            await docs.PutAsync(i, new Vector(floats), new VectorMetadata
            {
                Id = i,
                Collection = "documents",
                Payload = new() { ["title"] = $"Document {i}" }
            });
        }

        // Snapshot für konsistenten Query
        using var snapshot = store.CreateSnapshot();
        Console.WriteLine($"Snapshot created at {snapshot.Timestamp}");
        Console.WriteLine($"Collections: {string.Join(", ", snapshot.CollectionNames)}");

        // Query
        var queryFloats = new float[1536];
        for (int j = 0; j < queryFloats.Length; j++)
            queryFloats[j] = (float)random.NextDouble();

        var query = new Vector(queryFloats);

        Console.WriteLine("\nTop 5 Ergebnisse (HNSW):");
        await foreach (var result in docs.SearchHnswAsync(query, topK: 5, ef: 64))
        {
            Console.WriteLine($"  ID={result.Id}, Distance={result.Score:F4}");
        }

        // Checkpoint
        await store.CheckpointAsync();
    }
}
