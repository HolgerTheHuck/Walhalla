// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Walhalla.VectorStore;
using Walhalla.VectorStore.Filtering;
using Walhalla.VectorStore.Indexes;

// ═══════════════════════════════════════════════════════════════
// Sample.Embedded – Direkte Nutzung von Walhalla.VectorStore
// ═══════════════════════════════════════════════════════════════
// Kein Server, kein Docker, keine Cloud – nur eine Datei auf Disk.
// Dieses Sample zeigt einen kleinen Embedded-Flow mit Vector Search,
// persistentem FullText-Filter und Reopen ohne erneutes Index-Setup.
// ═══════════════════════════════════════════════════════════════

var dbPath = Path.Combine(Path.GetTempPath(), $"walhalla-sample-embedded-{Guid.NewGuid():N}");
Console.WriteLine($"Sample.Embedded startet...\n");
Console.WriteLine($"DB-Pfad: {dbPath}\n");

try
{
    using (var store = new EmbeddedVectorStore(dbPath))
    {
        var docs = store.GetOrCreateCollection(
            name: "knowledge",
            dimension: 4,
            metric: DistanceMetric.Cosine,
            enableHnsw: true,
            hnswOptions: new HnswOptions { M = 16, EfConstruction = 200 },
            payloadIndexOptions: new PayloadIndexOptions
            {
                PersistentFullText = true,
                PersistentMatch = true,
            });

        Console.WriteLine($"Collection '{docs.Name}' bereit. Dimension: {docs.Dimension}, Metrik: {docs.DefaultMetric}\n");

        await docs.UpsertAsync(1, new Vector(new[] { 1.00f, 0.00f, 0.00f, 0.00f }), new()
        {
            ["title"] = "Agent memory basics",
            ["body"] = "agent memory stores local experiences and summaries",
            ["category"] = "architecture"
        });
        await docs.UpsertAsync(2, new Vector(new[] { 0.97f, 0.03f, 0.00f, 0.00f }), new()
        {
            ["title"] = "Shared experience exchange",
            ["body"] = "agent memory exchange helps coordination between tools",
            ["category"] = "distributed"
        });
        await docs.UpsertAsync(3, new Vector(new[] { 0.90f, 0.10f, 0.00f, 0.00f }), new()
        {
            ["title"] = "Vector database overview",
            ["body"] = "vector storage and indexing for retrieval augmented generation",
            ["category"] = "database"
        });
        await docs.UpsertAsync(4, new Vector(new[] { 0.00f, 1.00f, 0.00f, 0.00f }), new()
        {
            ["title"] = "Garden notes",
            ["body"] = "watering tomatoes and peppers in the greenhouse",
            ["category"] = "private"
        });

        var query = new Vector(new[] { 0.98f, 0.02f, 0.00f, 0.00f });
        var fullTextFilter = new FilterClause(
            new Condition[] { new FullTextCondition("body", "agent memory") },
            null,
            null);

        Console.WriteLine("Vector Search ohne Filter (HNSW):");
        await PrintResultsAsync(docs.SearchHnswAsync(query, topK: 3, ef: 32));

        Console.WriteLine("\nVector Search + FullText-Filter (HNSW):");
        await PrintResultsAsync(docs.SearchHnswAsync(query, topK: 3, ef: 32, filter: fullTextFilter));

        await store.CheckpointAsync();
        var manifest = docs.GetManifest();
        Console.WriteLine($"\nCheckpoint geschrieben. Disk-Groesse: {store.GetDiskSize() / 1024:N0} KB");
        Console.WriteLine($"Manifest: warm={manifest.PayloadIndexWarm}, payloadIndexVersion={manifest.PayloadIndexVersion}, changeSequence={manifest.ChangeSequence}");
    }

    Console.WriteLine("\nStore neu oeffnen...");

    using (var reopenedStore = new EmbeddedVectorStore(dbPath))
    {
        var reopenedDocs = reopenedStore.GetOrCreateCollection(
            name: "knowledge",
            dimension: 4,
            metric: DistanceMetric.Cosine,
            enableHnsw: true);

        var query = new Vector(new[] { 0.98f, 0.02f, 0.00f, 0.00f });
        var fullTextFilter = new FilterClause(
            new Condition[] { new FullTextCondition("body", "agent memory") },
            null,
            null);

        Console.WriteLine("FullText nach Reopen ohne erneute PayloadIndexOptions:");
        await PrintResultsAsync(reopenedDocs.SearchExactAsync(query, topK: 3, filter: fullTextFilter));
        Console.WriteLine("Hinweis: Der Payload-FullText-Index ist persistent; ANN-Indizes wie HNSW bleiben aktuell In-Memory und koennen bei Bedarf neu aufgebaut werden.");
    }
}
finally
{
    if (Directory.Exists(dbPath))
        Directory.Delete(dbPath, recursive: true);

    Console.WriteLine($"\nTemp-DB entfernt: {dbPath}");
    Console.WriteLine("Sample.Embedded beendet.");
}

static async Task PrintResultsAsync(IAsyncEnumerable<VectorSearchResult> results)
{
    await foreach (var result in results)
    {
        string title = ReadPayloadValue(result.Metadata?.Payload, "title");
        string category = ReadPayloadValue(result.Metadata?.Payload, "category");
        Console.WriteLine($"  ID={result.Id}, Score={result.Score:F4}, Title={title}, Category={category}");
    }
}

static string ReadPayloadValue(Dictionary<string, object>? payload, string key)
{
    if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
        return "?";

    return value.ToString() ?? "?";
}
