// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

// ═══════════════════════════════════════════════════════════════
// Sample.Http – REST-API Client fuer Walhalla.VectorStore
// ═══════════════════════════════════════════════════════════════
// Erwartet einen laufenden Server:
//   dotnet run --project Walhalla.VectorStore.Api
// Default: http://localhost:5000
// ═══════════════════════════════════════════════════════════════

const string BaseUrl = "http://localhost:5000";
const string ApiKey = "walhalla-dev-key";

Console.WriteLine("Sample.Http startet...\n");
Console.WriteLine($"API-URL: {BaseUrl}");
Console.WriteLine("Stelle sicher, dass der Server laeuft: dotnet run --project Walhalla.VectorStore.Api\n");

using var http = new HttpClient();
http.DefaultRequestHeaders.Add("X-API-Key", ApiKey);

// 1. Health-Check
Console.WriteLine("Health-Check...");
try
{
    var stats = await http.GetFromJsonAsync<JsonElement>($"{BaseUrl}/api/stats");
    Console.WriteLine($"  Server erreichbar. Collections: {stats.GetProperty("collections").GetInt32()}, Vektoren: {stats.GetProperty("totalVectors").GetInt32()}\n");
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"  FEHLER: Server nicht erreichbar ({ex.Message})");
    Console.WriteLine("  Starte zuerst: dotnet run --project Walhalla.VectorStore.Api");
    return;
}

// 2. Collection erstellen
Console.WriteLine("Collection erstellen...");
var createResponse = await http.PostAsJsonAsync($"{BaseUrl}/api/collections", new
{
    name = "products",
    dimension = 128,
    metric = "Cosine",
    enableHnsw = true
});
createResponse.EnsureSuccessStatusCode();
var collection = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine($"  Erstellt: {collection.GetProperty("name").GetString()}, Dimension: {collection.GetProperty("dimension").GetInt32()}\n");

// 3. Vektoren einfuegen
Console.WriteLine("Vektoren einfuegen...");
var random = new Random(42);
var sw = Stopwatch.StartNew();

for (ulong i = 1; i <= 200; i++)
{
    var vector = new float[128];
    for (int j = 0; j < vector.Length; j++)
        vector[j] = (float)(random.NextDouble() * 2 - 1);

    var category = (i % 4) switch
    {
        0 => "electronics",
        1 => "clothing",
        2 => "food",
        _ => "books"
    };

    var putResponse = await http.PostAsJsonAsync($"{BaseUrl}/api/collections/products/vectors", new
    {
        id = i,
        vector,
        metadata = new Dictionary<string, object>
        {
            ["name"] = $"Product {i}",
            ["category"] = category,
            ["price"] = random.Next(10, 500)
        }
    });
    putResponse.EnsureSuccessStatusCode();
}

sw.Stop();
Console.WriteLine($"  200 Vektoren eingefuegt in {sw.ElapsedMilliseconds} ms\n");

// 4. Einzelnen Vektor abrufen
Console.WriteLine("Vektor ID=42 abrufen...");
var getResponse = await http.GetAsync($"{BaseUrl}/api/collections/products/vectors/42");
getResponse.EnsureSuccessStatusCode();
var entry = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine($"  ID={entry.GetProperty("id").GetUInt64()}, Dimension={entry.GetProperty("dimension").GetInt32()}");
if (entry.TryGetProperty("metadata", out var meta))
{
    Console.WriteLine($"  Metadata: name={meta.GetProperty("name").GetString()}, category={meta.GetProperty("category").GetString()}\n");
}

// 5. HNSW-Suche
var query = new float[128];
for (int j = 0; j < query.Length; j++)
    query[j] = (float)(random.NextDouble() * 2 - 1);

Console.WriteLine("HNSW-Suche (Top 5):");
sw.Restart();
var searchResponse = await http.PostAsJsonAsync($"{BaseUrl}/api/collections/products/search/hnsw", new
{
    vector = query,
    topK = 5,
    ef = 64
});
searchResponse.EnsureSuccessStatusCode();
var searchResults = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
sw.Stop();

foreach (var r in searchResults.EnumerateArray())
{
    var id = r.GetProperty("id").GetUInt64();
    var score = r.GetProperty("score").GetSingle();
    var name = r.GetProperty("metadata").GetProperty("name").GetString();
    Console.WriteLine($"  ID={id}, Score={score:F4}, Name={name}");
}
Console.WriteLine($"  Dauer: {sw.ElapsedMilliseconds} ms\n");

// 6. Exakte Suche
Console.WriteLine("Exakte Suche (Top 3):");
sw.Restart();
var exactResponse = await http.PostAsJsonAsync($"{BaseUrl}/api/collections/products/search/exact", new
{
    vector = query,
    topK = 3
});
exactResponse.EnsureSuccessStatusCode();
var exactResults = await exactResponse.Content.ReadFromJsonAsync<JsonElement>();
sw.Stop();

foreach (var r in exactResults.EnumerateArray().Take(3))
{
    Console.WriteLine($"  ID={r.GetProperty("id").GetUInt64()}, Score={r.GetProperty("score").GetSingle():F4}");
}
Console.WriteLine($"  Dauer: {sw.ElapsedMilliseconds} ms\n");

// 7. Stats abrufen
Console.WriteLine("Stats abrufen...");
var finalStats = await http.GetFromJsonAsync<JsonElement>($"{BaseUrl}/api/stats");
Console.WriteLine($"  Collections: {finalStats.GetProperty("collections").GetInt32()}");
Console.WriteLine($"  Total Vectors: {finalStats.GetProperty("totalVectors").GetInt32()}");
Console.WriteLine($"  Uptime: {finalStats.GetProperty("uptime").GetString()}\n");

// 8. Cleanup
Console.WriteLine("Collection loeschen...");
var deleteResponse = await http.DeleteAsync($"{BaseUrl}/api/collections/products");
deleteResponse.EnsureSuccessStatusCode();
Console.WriteLine("  Geloescht.\n");

Console.WriteLine("Sample.Http beendet.");
