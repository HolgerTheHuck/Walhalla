// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Walhalla.VectorStore.Client;
using Walhalla.VectorStore.Client.Models;

// ═══════════════════════════════════════════════════════════════
// Sample.Grpc – gRPC-Client fuer Walhalla.VectorStore
// ═══════════════════════════════════════════════════════════════
// Erwartet einen laufenden gRPC-Server:
//   dotnet run --project Walhalla.VectorStore.Api
// Default: http://localhost:5000
// ═══════════════════════════════════════════════════════════════

const string ServerAddress = "http://localhost:5000";

Console.WriteLine("Sample.Grpc startet...\n");
Console.WriteLine($"Server: {ServerAddress}");
Console.WriteLine("Stelle sicher, dass der Server laeuft: dotnet run --project Walhalla.VectorStore.Api\n");

// 1. Client erstellen
using var client = new WalhallaClient(ServerAddress);

// 2. Health-Check: Liste aller Collections
Console.WriteLine("Collections auflisten...");
try
{
    var collections = await client.ListCollectionsAsync();
    Console.WriteLine($"  Server erreichbar. Aktuelle Collections: {collections.Count}\n");
}
catch (Grpc.Core.RpcException ex)
{
    Console.WriteLine($"  FEHLER: Server nicht erreichbar ({ex.Status.Detail})");
    Console.WriteLine("  Starte zuerst: dotnet run --project Walhalla.VectorStore.Api");
    return;
}

// 3. Collection erstellen
Console.WriteLine("Collection 'articles' erstellen...");
var collection = await client.CreateCollectionAsync(
    name: "articles",
    dimension: 128,
    metric: DistanceMetric.Cosine,
    enableHnsw: true);

Console.WriteLine($"  Erstellt: {collection.Name}, Dimension: {collection.Dimension}, HNSW: {collection.HnswEnabled}\n");

// 4. Vektoren einfuegen
Console.WriteLine("Vektoren einfuegen...");
var random = new Random(42);
var sw = Stopwatch.StartNew();

for (ulong i = 1; i <= 200; i++)
{
    var vector = new float[128];
    for (int j = 0; j < vector.Length; j++)
        vector[j] = (float)(random.NextDouble() * 2 - 1);

    var category = (i % 3) switch
    {
        0 => "tech",
        1 => "science",
        _ => "politics"
    };

    await client.UpsertAsync(
        collection: "articles",
        id: i,
        vector: vector,
        metadata: new Dictionary<string, object>
        {
            ["title"] = $"Article {i}",
            ["category"] = category,
            ["views"] = random.Next(100, 10000)
        });
}

sw.Stop();
Console.WriteLine($"  200 Vektoren eingefuegt in {sw.ElapsedMilliseconds} ms\n");

// 5. Einzelnen Vektor abrufen
Console.WriteLine("Vektor ID=42 abrufen...");
var entry = await client.GetAsync("articles", 42);
if (entry != null)
{
    Console.WriteLine($"  ID={entry.Id}");
    if (entry.Metadata is not null)
    {
        Console.WriteLine($"  Metadata: title={entry.Metadata.GetValueOrDefault("title")}, category={entry.Metadata.GetValueOrDefault("category")}\n");
    }
}

// 6. HNSW-Suche
var query = new float[128];
for (int j = 0; j < query.Length; j++)
    query[j] = (float)(random.NextDouble() * 2 - 1);

Console.WriteLine("HNSW-Suche (Top 5):");
sw.Restart();
var hnswResults = await client.SearchAsync("articles", query, topK: 5, ef: 64);
sw.Stop();

foreach (var r in hnswResults)
{
    var title = r.Metadata?.GetValueOrDefault("title") ?? "?";
    var category = r.Metadata?.GetValueOrDefault("category") ?? "?";
    Console.WriteLine($"  ID={r.Id}, Score={r.Score:F4}, Title={title}, Category={category}");
}
Console.WriteLine($"  Dauer: {sw.ElapsedMilliseconds} ms\n");

// 7. Exakte Suche
Console.WriteLine("Exakte Suche (Top 3):");
sw.Restart();
var exactResults = await client.SearchExactAsync("articles", query, topK: 3);
sw.Stop();

foreach (var r in exactResults.Take(3))
{
    Console.WriteLine($"  ID={r.Id}, Score={r.Score:F4}");
}
Console.WriteLine($"  Dauer: {sw.ElapsedMilliseconds} ms\n");

// 8. Cleanup
Console.WriteLine("Collection loeschen...");
await client.DeleteCollectionAsync("articles");
Console.WriteLine("  Geloescht.\n");

Console.WriteLine("Sample.Grpc beendet.");
