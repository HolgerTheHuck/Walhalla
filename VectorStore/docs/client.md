# gRPC-Client – Walhalla.VectorStore.Client

Das Paket `Walhalla.VectorStore.Client` bietet einen gRPC-Client für den Zugriff auf einen laufenden `Walhalla.VectorStore.Api`-Server.

## Installation

```bash
dotnet add package Walhalla.VectorStore.Client
```

## Schnellstart

```csharp
using Walhalla.VectorStore.Client;

var client = new WalhallaClient("http://localhost:5000", apiKey: "walhalla-dev-key");

// Collection erstellen
await client.CreateCollectionAsync("documents", 1536, Models.DistanceMetric.Cosine);

// Vektor einfügen
await client.UpsertAsync("documents", 1, new[] { 0.1f, 0.2f, ... },
    new Dictionary<string, object> { ["title"] = "Hello" });

// Suchen
var results = await client.SearchAsync("documents", new[] { 0.1f, 0.2f, ... }, topK: 5);
foreach (var r in results)
    Console.WriteLine($"ID={r.Id}, Score={r.Score:F4}");

client.Dispose();
```

---

## Verbindung

### Mit API-Key

```csharp
var client = new WalhallaClient("http://localhost:5000", apiKey: "walhalla-dev-key");
```

### Bestehenden Channel wiederverwenden

```csharp
using var channel = GrpcChannel.ForAddress("http://localhost:5000");
var client = new WalhallaClient(channel);
```

### Mit eigenem HttpClient

```csharp
using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5000") };
using var channel = GrpcChannel.ForAddress("http://localhost:5000");
var client = new WalhallaClient(channel, http, apiKey: "walhalla-dev-key");
```

---

## Collections

```csharp
// Erstellen
await client.CreateCollectionAsync("docs", 1536, Models.DistanceMetric.Cosine, enableHnsw: true);

// Auflisten
var collections = await client.ListCollectionsAsync();

// Löschen
await client.DeleteCollectionAsync("docs");
```

---

## Vektoren

```csharp
// Einfügen
await client.UpsertAsync("docs", id: 1, vector: floats, metadata: dict);

// Batch
await client.UpsertBatchAsync("docs", items);

// Lesen
var entry = await client.GetAsync("docs", 1);

// Löschen
await client.DeleteAsync("docs", 1);
```

---

## Suche

```csharp
// HNSW
var results = await client.SearchAsync("docs", query, topK: 10);

// Mit Filter
var results = await client.SearchAsync("docs", query, topK: 10, filter: "category == 'tech'");

// Exakte Suche
var results = await client.SearchExactAsync("docs", query, topK: 10);

// IVF
var results = await client.SearchIvfAsync("docs", query, topK: 10, nprobe: 5);
```

---

## Change-Feed (SSE)

Der Change-Feed streamt alle Mutationen einer Collection als Server-Sent Events:

```csharp
await foreach (var change in client.WatchChangesAsync("docs", afterSequence: 0))
{
    Console.WriteLine($"[{change.Sequence}] {change.Operation}: {change.Items.Count} items");
}
```

---

## Disposal

`WalhallaClient` verwaltet Channel und HttpClient. Wenn du sie selbst erstellst, disposet der Client sie:

```csharp
using var client = new WalhallaClient("http://localhost:5000");
// ...
// client.Dispose() schließt Channel + HttpClient
```

Wenn du einen externen Channel übergibst, wird dieser **nicht** disposet:

```csharp
using var channel = GrpcChannel.ForAddress("http://localhost:5000");
{
    using var client = new WalhallaClient(channel);
    // channel bleibt offen
}
```
