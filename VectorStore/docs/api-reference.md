# API-Referenz

Kompakte Übersicht der öffentlichen APIs pro Paket. Für Tutorials siehe die [Dokumentations-Startseite](README.md).

---

## Walhalla.VectorStore

### EmbeddedVectorStore

| Member | Beschreibung |
|---|---|
| `EmbeddedVectorStore(string path)` | Store im Verzeichnis öffnen/erstellen |
| `GetOrCreateCollection(name, dim, metric, ...)` | Collection erstellen oder holen |
| `GetCollection(name)` | Existierende Collection (oder null) |
| `GetCollections()` | Alle Collections |
| `DeleteCollection(name)` | Collection löschen |
| `CreateSnapshot()` | Point-in-Time Snapshot |
| `CheckpointAsync()` | Pending Changes auf Disk schreiben |
| `GetDiskSize()` | Gesamtgröße in Bytes |

### VectorCollection (IVectorRepository)

| Member | Beschreibung |
|---|---|
| `Name`, `Dimension`, `Count` | Properties |
| `PutAsync(id, vector, metadata)` | Vektor einfügen/aktualisieren |
| `PutBatchAsync(items)` | Batch-Insert |
| `GetAsync(id)` | Vektor + Metadaten lesen |
| `DeleteAsync(id)` | Vektor löschen |
| `ExistsAsync(id)` | Existenz prüfen |
| `EnumerateIdsAsync()` | Alle IDs aufzählen |
| `SearchHnswAsync(query, topK, ef, filter)` | HNSW-ANN-Suche |
| `SearchIvfAsync(query, topK, nprobe, filter)` | IVF-ANN-Suche |
| `SearchExactAsync(query, topK, filter)` | Brute-Force-Suche |
| `SearchTextAsync(field, query, topK, mode)` | Volltextsuche |
| `SearchHybridAsync(field, text, vector, topK, ...)` | Hybrid (Vektor + Text) |
| `RebuildIndexAsync(progress)` | Alle Indizes neu aufbauen |
| `ReadChangesAsync(afterSequence)` | Change-Feed streamen |

### Vector (Struct)

| Member | Beschreibung |
|---|---|
| `Vector(float[] data)` | Aus float-Array |
| `Vector(int dimension)` | Null-Vektor |
| `Dimension` | Anzahl Dimensionen |
| `Span` | `ReadOnlySpan<float>` Zugriff |
| `ToByteArray()` | Little-Endian float32 serialisieren |
| `FromByteArray(data, dim)` | Deserialisieren |

### VectorMetadata

| Property | Beschreibung |
|---|---|
| `Id` | Vektor-ID |
| `Collection` | Collection-Name |
| `CreatedAt` | Erstellungszeitpunkt |
| `Payload` | Beliebige Key-Value-Daten |

### VectorSearchResult

| Property | Beschreibung |
|---|---|
| `Id` | Treffer-ID |
| `Score` | Distanz/Score (je nach Metrik) |
| `Metadata` | Metadaten des Treffers |

### Enums

| Enum | Werte |
|---|---|
| `DistanceMetric` | `Euclidean`, `Cosine`, `DotProduct` |
| `FullTextQueryMode` | `All` (AND), `Any` (OR) |

### Optionen

| Klasse | Properties |
|---|---|
| `HnswOptions` | `M`, `EfConstruction`, `EfSearch`, `AsyncIndexing`, `VectorCacheSize`, `Dimension` |
| `IvfOptions` | `NClusters`, `Nprobe`, `MaxIterations` |
| `PayloadIndexOptions` | `PersistentMatch`, `PersistentRange`, `PersistentFullText`, `PersistentGeo` |

### Extension-Methoden (VectorCollectionExtensions)

| Methode | Beschreibung |
|---|---|
| `UpsertAsync(collection, id, vector, metadata)` | Convenience: Metadata als Dictionary |
| `SearchAsync(collection, query, topK, ef, nprobe)` | Automatischer Fallback: HNSW → IVF → Exact |
| `SearchAsync(collection, query, topK, filter)` | Brute-Force mit Metadata-Filter |
| `GetAllAsync(collection)` | Alle Einträge |
| `CountAsync(collection, filter)` | Zählen (mit/ohne Filter) |

---

## Walhalla.VectorStore.Client

### WalhallaClient

| Member | Beschreibung |
|---|---|
| `WalhallaClient(string address, string apiKey)` | Neuer Client mit API-Key |
| `WalhallaClient(GrpcChannel channel)` | Mit bestehendem Channel |
| `CreateCollectionAsync(name, dim, metric, ...)` | Collection erstellen |
| `ListCollectionsAsync()` | Collections auflisten |
| `DeleteCollectionAsync(name)` | Collection löschen |
| `UpsertAsync(collection, id, vector, metadata)` | Vektor einfügen |
| `UpsertBatchAsync(collection, items)` | Batch |
| `GetAsync(collection, id)` | Vektor lesen |
| `DeleteAsync(collection, id)` | Vektor löschen |
| `SearchAsync(collection, query, topK, filter)` | HNSW-Suche |
| `SearchExactAsync(collection, query, topK, filter)` | Exakte Suche |
| `SearchIvfAsync(collection, query, topK, nprobe)` | IVF-Suche |
| `WatchChangesAsync(collection, afterSequence)` | SSE Change-Feed als AsyncEnumerable |

---

## Walhalla.VectorStore.Microsoft.Extensions.VectorData

### WalhallaVectorStore

Erbt von `Microsoft.Extensions.VectorData.VectorStore`.

| Member | Beschreibung |
|---|---|
| `WalhallaVectorStore(string path)` | Store erstellen |
| `WalhallaVectorStore(EmbeddedVectorStore store, options?)` | Mit bestehendem Store |
| `GetCollection<TKey, TRecord>(name, definition)` | Typisierte Collection |
| `GetDynamicCollection(name, definition)` | Dynamische Collection |
| `ListCollectionNamesAsync()` | Namen auflisten |
| `CollectionExistsAsync(name)` | Prüfen |
| `EnsureCollectionDeletedAsync(name)` | Löschen |
| `GetService(typeof(EmbeddedVectorStore))` | Zugriff auf underlying Store |

### WalhallaVectorStoreCollection<TKey, TRecord>

Erbt von `VectorStoreCollection<TKey, TRecord>`.

| Member | Beschreibung |
|---|---|
| `UpsertAsync(record)` | Eintrag einfügen/aktualisieren |
| `UpsertAsync(records)` | Batch |
| `GetAsync(key)` | Einzelnen Eintrag lesen |
| `GetAsync(keys)` | Mehrere lesen |
| `DeleteAsync(key)` | Löschen |
| `DeleteAsync(keys)` | Batch-Löschen |
| `SearchAsync(vector, top, options)` | Vektor-Suche |
| `GetAsync(predicate, top, options)` | Gefilterte Suche |

---

## Walhalla.VectorStore.Embeddings

### TextVectorCollection

Erweitert `VectorCollection` um Text-Operationen.

| Member | Beschreibung |
|---|---|
| `UpsertAsync(id, text, metadata)` | Text einfügen (automatisch embeddet) |
| `SearchTextAsync(text, topK)` | Text-Suche |

### Extension-Methoden

| Methode | Beschreibung |
|---|---|
| `GetOrCreateTextCollection(store, name, generator, metric, options?)` | Text-Collection erstellen |

---

## Walhalla.VectorStore.Embeddings.Onnx

### OnnxEmbeddingGenerator

Implementiert `IEmbeddingGenerator<string, Embedding<float>>`.

| Member | Beschreibung |
|---|---|
| `OnnxEmbeddingGenerator(modelPath, tokenizerPath, dim)` | Generator erstellen |
| `GenerateAsync(text)` | Einzelnen Text embedden |
| `GenerateAsync(texts)` | Batch-Embedding |

### HuggingFaceModelDownloader

| Member | Beschreibung |
|---|---|
| `HuggingFaceModelDownloader(modelId, outputDir)` | Downloader erstellen |
| `DownloadAsync()` | Modell + Tokenizer herunterladen |

---

## Walhalla.Storage

### WalhallaStore

| Member | Beschreibung |
|---|---|
| `WalhallaStore(WalhallaOptions)` | Store erstellen |
| `PutAsync(key, value)` | Key-Value schreiben |
| `TryGetAsync(key)` | Key lesen (oder null) |
| `DeleteAsync(key)` | Key löschen |
| `ScanRangeAsync(start, end)` | Range-Scan |
| `ScanPrefixAsync(prefix)` | Prefix-Scan |
| `CheckpointAsync()` | Auf Disk schreiben |
| `Dispose()` | Store schließen |

### WalhallaOptions

| Property | Beschreibung |
|---|---|
| `RootPath` | Speicherpfad |
| `StorageMode` | `Durable`, `WriteBack`, `InMemory`, `Ephemeral` |

---

## Walhalla.Storage.Blobs

### BlobStore

| Member | Beschreibung |
|---|---|
| `BlobStore(BlobStoreOptions)` | BlobStore erstellen |
| `PutAsync(key, value)` | Blob speichern |
| `TryGetAsync(key)` | Blob lesen |
| `DeleteAsync(key)` | Blob löschen |
| `ScanPrefixAsync(prefix, ct)` | Prefix-Scan über Keys |
| `CheckpointAsync(ct)` | Checkpoint |
| `Dispose()` | Store schließen |

### BlobStoreOptions

| Property | Beschreibung |
|---|---|
| `RootPath` | Speicherpfad |
| `MaxBlobSizeInTree` | Schwellwert für Blob-Auslagerung |
