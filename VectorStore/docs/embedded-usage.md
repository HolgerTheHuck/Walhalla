# Embedded-Nutzung von Walhalla.VectorStore

Das Paket `Walhalla.VectorStore` ist der Einstiegspunkt für lokale, datei-basierte Vektor-Speicherung. Kein Server, kein Docker, keine Cloud – nur ein Verzeichnis auf Disk.

## Installation

```bash
dotnet add package Walhalla.VectorStore
```

## Schnellstart

```csharp
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;

// 1. Store öffnen – ein Verzeichnis, das ist alles
using var store = new EmbeddedVectorStore("my_data");

// 2. Collection erstellen
var docs = store.GetOrCreateCollection("documents", dimension: 1536);

// 3. Vektor einfügen
await docs.UpsertAsync(1, new Vector(embedding), new()
{
    ["title"] = "README.md",
    ["category"] = "documentation"
});

// 4. Suchen
var results = await docs.SearchAsync(new Vector(query), topK: 5);
foreach (var r in results)
    Console.WriteLine($"ID={r.Id}, Score={r.Score:F4}");
```

---

## EmbeddedVectorStore

`EmbeddedVectorStore` ist die Top-Level-API. Es verwaltet einen `BlobStore` (WAL + B+Tree) und einen `VectorCollectionManager`.

```csharp
using var store = new EmbeddedVectorStore("pfad/zum/verzeichnis");
```

**Wichtige Methoden:**

| Methode | Zweck |
|---|---|
| `GetOrCreateCollection(name, dimension, metric, ...)` | Collection erstellen oder öffnen |
| `GetCollection(name)` | Existierende Collection holen (null wenn nicht vorhanden) |
| `GetCollections()` | Alle Collections auflisten |
| `DeleteCollection(name)` | Collection löschen |
| `CreateSnapshot()` | Point-in-Time Snapshot über alle Collections |
| `CheckpointAsync()` | Alle pending Changes auf Disk schreiben |
| `GetDiskSize()` | Gesamtgröße des Stores in Bytes |

---

## Collection erstellen

```csharp
var docs = store.GetOrCreateCollection(
    "documents",
    dimension: 1536,
    metric: DistanceMetric.Cosine,
    enableHnsw: true,
    hnswOptions: new HnswOptions
    {
        M = 16,
        EfConstruction = 200,
        EfSearch = 128,
        AsyncIndexing = false  // true = Hintergrund-Indexing via Channel
    });
```

### Parameter

| Parameter | Standard | Bedeutung |
|---|---|---|
| `metric` | `Cosine` | `Euclidean`, `Cosine`, `DotProduct` |
| `enableHnsw` | `true` | HNSW-Graph für schnelle ANN-Suche |
| `enableIvf` | `false` | IVFFlat-Index für RAM-effiziente Suche |
| `hnswOptions` | — | `M`, `EfConstruction`, `EfSearch`, `AsyncIndexing`, `VectorCacheSize` |
| `ivfOptions` | — | `NClusters`, `Nprobe`, `MaxIterations` |
| `payloadIndexOptions` | — | Konfiguration für Metadata-Index (Match, Range, FullText, Geo) |

**Cosine-Vektoren werden automatisch normalisiert** bei Insertion. Zur Query-Zeit wird Cosine-Similarity auf DotProduct reduziert.

---

## CRUD-Operationen

### Einfügen / Aktualisieren

```csharp
// Einzelner Vektor
await docs.UpsertAsync(1, new Vector(floats), new() { ["title"] = "A" });

// Mit expliziten Metadaten
await docs.PutAsync(1, new Vector(floats), new VectorMetadata
{
    Id = 1,
    Collection = "documents",
    Payload = new() { ["title"] = "A", ["category"] = "tech" }
});

// Batch
var items = Enumerable.Range(0, 1000).Select(i => (
    (ulong)i,
    new Vector(embeddings[i]),
    new VectorMetadata { Id = (ulong)i, Collection = "documents", Payload = new() { ["index"] = i } }
));
await docs.PutBatchAsync(items);
```

### Lesen

```csharp
var entry = await docs.GetAsync(1);
if (entry is not null)
{
    Console.WriteLine($"Dimension: {entry.Vector.Dimension}");
    Console.WriteLine($"Metadata: {entry.Metadata?.Payload?["title"]}");
}
```

### Löschen

```csharp
await docs.DeleteAsync(1);
```

### Alle IDs aufzählen

```csharp
await foreach (var id in docs.EnumerateIdsAsync())
    Console.WriteLine(id);
```

---

## Suche

### Automatischer Fallback

`SearchAsync` (Extension-Methode) wählt automatisch den besten verfügbaren Index:

```csharp
var results = await docs.SearchAsync(new Vector(query), topK: 10);
// Fallback: HNSW → IVF → Exact
```

### Explizite Suchmethoden

| Methode | Wann nutzen? |
|---|---|
| `SearchHnswAsync(query, topK, ef)` | Schnelle ANN-Suche (hoher Recall) |
| `SearchIvfAsync(query, topK, nprobe)` | RAM-effiziente ANN (IoT/Edge) |
| `SearchExactAsync(query, topK, filter)` | Brute-Force (langsam, 100% korrekt) |

```csharp
// HNSW mit angepasstem ef
await foreach (var r in docs.SearchHnswAsync(query, topK: 10, ef: 256))
    Console.WriteLine($"{r.Id}: {r.Score:F4}");

// IVF mit nprobe
await foreach (var r in docs.SearchIvfAsync(query, topK: 10, nprobe: 10))
    Console.WriteLine($"{r.Id}: {r.Score:F4}");

// Exact + Filter
var filter = FilterParser.Parse("category == 'tech' && score >= 0.5");
await foreach (var r in docs.SearchExactAsync(query, topK: 10, filter))
    Console.WriteLine($"{r.Id}: {r.Score:F4}");
```

### Text-Suche (Full-Text)

```csharp
await foreach (var r in docs.SearchTextAsync("title", "hello world", topK: 10, mode: FullTextQueryMode.All))
    Console.WriteLine($"{r.Id}: {r.Score:F4}");
```

### Hybrid-Suche (Vektor + Text)

```csharp
await foreach (var r in docs.SearchHybridAsync(
    field: "title",
    textQuery: "hello world",
    vectorQuery: query,
    topK: 10,
    textCandidateCount: 50,
    mode: FullTextQueryMode.All))
{
    Console.WriteLine($"{r.Id}: {r.Score:F4}");
}
```

---

## Metadata-Filterung

### Inline-Filter (Brute-Force)

```csharp
var results = await docs.SearchAsync(
    query, topK: 5,
    filter: meta => meta?["category"]?.ToString() == "tech");
```

### Filter-Parser (DSL)

```csharp
var filter = FilterParser.Parse("category == 'tech' && score >= 0.5");
var results = await docs.SearchExactAsync(query, topK: 10, filter);
```

Unterstützte Operatoren: `==`, `!=`, `<`, `>`, `<=`, `>=`, `~=` (Contains), `||`, `&&`, `!`.

### Payload-Index (beschleunigt)

Wenn `PayloadIndex` aktiviert ist (Standard: `enablePayloadIndex: true`), werden Metadata-Felder indexiert und gefilterte Exact-Suchen beschleunigt:

```csharp
var docs = store.GetOrCreateCollection(
    "documents", 1536, DistanceMetric.Cosine,
    enableHnsw: true,
    payloadIndexOptions: new PayloadIndexOptions
    {
        PersistentMatch = true,   // Match-Index (==, !=)
        PersistentRange = true,   // Range-Index (<, >, <=, >=)
        PersistentFullText = true // FullText-Index
    });
```

---

## Index-Verwaltung

### Index neu aufbauen

```csharp
await docs.RebuildIndexAsync(progress: new Progress<double>(p => Console.WriteLine($"{p:P0}")));
```

Das baut **alle** aktivierten Indizes (HNSW + IVF) neu auf.

### Async-Indexing (HNSW)

```csharp
var docs = store.GetOrCreateCollection(
    "documents", 1536,
    hnswOptions: new HnswOptions { AsyncIndexing = true });
```

Bei `AsyncIndexing = true` werden Insertions in einen Channel geschrieben und ein Background-Worker baut den HNSW-Graph inkrementell auf. Das beschleunigt Batch-Inserts massiv.

---

## Snapshots

Snapshots garantieren Point-in-Time-Konsistenz über alle Collections hinweg:

```csharp
using var snapshot = store.CreateSnapshot();

foreach (var name in snapshot.CollectionNames)
{
    var iter = snapshot.CreateIterator(name, dimension: 1536);
    await foreach (var record in iter)
    {
        Console.WriteLine($"{record.Id}: seq={record.SequenceNumber}");
    }
}
```

---

## Change-Feed (Events)

Jede Mutation erzeugt ein Change-Event. Das kann für Event-Sourcing oder Synchronisation genutzt werden:

```csharp
await foreach (var change in docs.ReadChangesAsync(afterSequence: 0))
{
    Console.WriteLine($"[{change.Sequence}] {change.Operation}: {change.Items.Count} items");
}
```

---

## Disposal

`EmbeddedVectorStore` und alle Collections implementieren `IDisposable`:

```csharp
using var store = new EmbeddedVectorStore("my_data");
// ... arbeiten ...
// store.Dispose() schließt Dateien und gibt Locks frei
```

Wichtig: Immer `store` vor dem Löschen des Verzeichnisses disposen.

---

## HNSW vs IVFFlat – Welchen Index wählen?

| Szenario | Empfohlener Index | Begründung |
|---|---|---|
| IoT / Edge / Embedded | **IVFFlat** | Sehr wenig RAM, Flash-Preis zählt |
| Desktop-App / Agent | **HNSW** | Hoher Recall, inkrementell |
| Server / >100k Vektoren | **HNSW** | Logarithmische Suche, skaliert beliebig |
| Hohe Dimensionen (1536D) | **HNSW** | IVF verliert bei hohen D Vorteil |
| Batch-Import, statisch | **IVFFlat** | Schneller Build |
| Streaming / Online | **HNSW** | Neue Vektoren sofort im Index |

Beide gleichzeitig aktivieren (Fallback-Strategie):

```csharp
var docs = store.GetOrCreateCollection(
    "documents", 512, DistanceMetric.Cosine,
    enableHnsw: true, enableIvf: true);

await docs.RebuildIndexAsync(); // Baut beide Indizes

// IVF für grobe Ergebnisse
var approx = await docs.SearchIvfAsync(query, topK: 50, nprobe: 3).ToListAsync();

// HNSW für finale Top-10
var exact = await docs.SearchHnswAsync(query, topK: 10, ef: 128).ToListAsync();
```
