# Walhalla.VectorStore

VectorDB auf Basis von Walhalla.Storage.Blobs – mehrere Collections, HNSW-Index, SIMD-Optimierung.

## Architektur

```
┌─────────────────────────────────────────────────────────┐
│  VectorCollectionManager                                │
│  ├── GetOrCreateCollection("docs", 1536)               │
│  ├── GetOrCreateCollection("images", 512)              │
│  └── CreateSnapshot() → Point-in-Time                  │
├─────────────────────────────────────────────────────────┤
│  VectorCollection (pro Collection)                      │
│  ├── Key: c:{name}:v:{id} → Vektor                     │
│  ├── Key: c:{name}:m:{id} → Metadaten                  │
│  ├── Key: c:{name}:s → Sequenznummer                  │
│  └── HnswIndex + LRU-Cache                             │
├─────────────────────────────────────────────────────────┤
│  Walhalla.Storage.Blobs (gemeinsamer Store)            │
│  └── Alle Collections in einem BlobStore              │
└─────────────────────────────────────────────────────────┘
```

## Features

| Feature | Status |
|---------|--------|
| SIMD AVX2 | ✅ L2, Cosine, DotProduct |
| HNSW Index | ✅ In-Memory, ID-Referenzen |
| Multiple Collections | ✅ Key-Prefix-Layout |
| Snapshot Isolation | ✅ Sequenznummern |
| LRU-Cache | ✅ Für HNSW-Lookups |
| Cosine-Normalisierung | ✅ Auto bei Put |

## Schnelleinstieg

```csharp
using var store = new BlobStore(new BlobStoreOptions { RootPath = "db" });
using var manager = new VectorCollectionManager(store);

// Collection erstellen
var docs = manager.GetOrCreateCollection("documents", 1536, DistanceMetric.Cosine);

// Einfügen
await docs.PutAsync(1, vector, new VectorMetadata { Collection = "documents" });

// HNSW-Suche
var results = await docs.SearchHnswAsync(query, topK: 10, ef: 64);

// Snapshot für konsistenten Scan
using var snapshot = manager.CreateSnapshot();
var iter = snapshot.CreateIterator("documents", 1536);
```

## Voraussetzung

`EnumerateIdsAsync()` und `SnapshotIterator` erfordern **Prefix-Scan** vom WalhallaStore. Implementieren Sie:

```csharp
// In WalhallaStore oder BlobStore
IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix);
```

Alternativ: Separate `id-list` Tabelle pro Collection pflegen.

## SQL-Integration (geplant)

```sql
CREATE TABLE documents (
    id BIGINT PRIMARY KEY,
    content TEXT,
    embedding VECTOR(1536) REFERENCES c:documents
);

CREATE INDEX idx_doc_embedding ON documents
USING hnsw (embedding vector_cosine_ops);

SELECT id, content, embedding <=> @query AS distance
FROM documents
WHERE category = 'tech'
ORDER BY embedding <=> @query
LIMIT 10;
```
