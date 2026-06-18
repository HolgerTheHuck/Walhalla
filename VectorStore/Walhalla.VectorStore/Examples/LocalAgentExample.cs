// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.VectorStore.Examples;

/// <summary>
/// Beispiel: Ein lokaler RAG-Agent mit embedded Vector-Store.
/// Kein Server, kein Docker, kein Cloud-API – nur eine Datei auf Disk.
/// </summary>
public static class LocalAgentExample
{
    public static async Task RunAsync()
    {
        // ═══════════════════════════════════════════════════════════════
        // 1. Store öffnen (oder erstellen) – einfach wie SQLite
        // ═══════════════════════════════════════════════════════════════
        using var store = new EmbeddedVectorStore("agent_memory");

        // ═══════════════════════════════════════════════════════════════
        // 2. Collection für Dokument-Embeddings
        // ═══════════════════════════════════════════════════════════════
        var docs = store.GetOrCreateCollection(
            name: "documents",
            dimension: 1536,          // z.B. OpenAI text-embedding-3-small
            metric: DistanceMetric.Cosine,
            enableHnsw: true,
            hnswOptions: new HnswOptions { M = 16, EfConstruction = 200 });

        Console.WriteLine($"Collection '{docs.Name}' bereit. Vektoren: {docs.Count}");

        // ═══════════════════════════════════════════════════════════════
        // 3. Dokumente einfügen (mit Metadata für Filterung)
        // ═══════════════════════════════════════════════════════════════
        var random = new Random();

        // Simuliere 100 Dokumente aus verschiedenen Kategorien
        for (ulong i = 1; i <= 100; i++)
        {
            var embedding = new float[1536];
            for (int j = 0; j < embedding.Length; j++)
                embedding[j] = (float)(random.NextDouble() * 2 - 1);

            var categoryIndex = (int)(i % 3);
            var category = categoryIndex switch
            {
                0 => "code",
                1 => "documentation",
                _ => "email"
            };

            await docs.UpsertAsync(i, new Vector(embedding), new()
            {
                ["title"] = $"Document {i}",
                ["category"] = category,
                ["author"] = $"user{(i % 5) + 1}",
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        Console.WriteLine($"100 Dokumente eingefügt. Gesamt: {docs.Count}");

        // ═══════════════════════════════════════════════════════════════
        // 4. Suche – einfach wie LINQ
        // ═══════════════════════════════════════════════════════════════
        var queryEmbedding = new float[1536];
        for (int j = 0; j < queryEmbedding.Length; j++)
            queryEmbedding[j] = (float)(random.NextDouble() * 2 - 1);

        var query = new Vector(queryEmbedding);

        // 4a. HNSW-Suche (schnell, approximativ)
        Console.WriteLine("\n🔍 HNSW-Suche (Top 5):");
        var hnswResults = await docs.SearchAsync(query, topK: 5, ef: 64);
        foreach (var r in hnswResults)
        {
            var title = r.Metadata?.Payload?.GetValueOrDefault("title") ?? "?";
            Console.WriteLine($"   ID={r.Id}, Score={r.Score:F4}, Title={title}");
        }

        // 4b. Gefilterte Suche – nur "code"-Dokumente
        Console.WriteLine("\n🔍 Gefilterte Suche (nur 'code'):");
        var filtered = await docs.SearchAsync(
            query,
            topK: 5,
            filter: meta => meta?.GetValueOrDefault("category")?.ToString() == "code");

        foreach (var r in filtered)
        {
            var cat = r.Metadata?.Payload?.GetValueOrDefault("category") ?? "?";
            Console.WriteLine($"   ID={r.Id}, Score={r.Score:F4}, Category={cat}");
        }

        // ═══════════════════════════════════════════════════════════════
        // 5. Snapshot für konsistente Queries
        // ═══════════════════════════════════════════════════════════════
        using var snapshot = store.CreateSnapshot();
        Console.WriteLine($"\n📸 Snapshot erstellt: {snapshot.Timestamp}");
        Console.WriteLine($"   Collections: {string.Join(", ", snapshot.CollectionNames)}");

        // ═══════════════════════════════════════════════════════════════
        // 6. Persistenz – explizit auf Disk schreiben
        // ═══════════════════════════════════════════════════════════════
        await store.CheckpointAsync();
        var size = store.GetDiskSize();
        Console.WriteLine($"\n💾 Checkpoint geschrieben. Store-Größe: {size / 1024:N0} KB");

        // ═══════════════════════════════════════════════════════════════
        // 7. Multi-Collection – z.B. separate Collections pro Agent/Session
        // ═══════════════════════════════════════════════════════════════
        var sessionStore = store.GetOrCreateCollection("session_42", dimension: 1536);
        await sessionStore.UpsertAsync(1, query, new() { ["query"] = "Wie funktioniert HNSW?" });

        Console.WriteLine($"\n✅ Agent-Speicher bereit. Collections: {store.GetCollections().Count}");
    }
}
