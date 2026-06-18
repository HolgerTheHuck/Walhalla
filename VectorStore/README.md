# Walhalla.VectorStore

> **Der erste echte embedded Vector-Store für .NET**  
> SQLite für Vektoren – zero-config, datei-basiert, standalone.

---

## 🎯 Vision

Für .NET gibt es bisher **keinen** echten embedded Vector-Store:

| Produkt | Embedded | .NET-Native | Serverlos |
|---------|----------|-------------|-----------|
| Qdrant | ❌ | ❌ Client-only | ❌ |
| Milvus | ❌ | ❌ Client-only | ❌ |
| Weaviate | ❌ | ❌ Client-only | ❌ |
| Chroma | ⚠️ Python-first | ❌ | ⚠️ |
| SQLite + VSS | ⚠️ Extension | ❌ | ✅ |
| **Walhalla** | ✅ **Ja** | ✅ **Ja** | ✅ **Ja** |

**Walhalla.VectorStore** schließt diese Lücke. Ein lokaler Agent, eine Desktop-App, ein Game oder ein Tool kann direkt Vektoren speichern, indizieren und suchen – ohne Docker, ohne Cloud-API, ohne Server-Prozess.

---

## ✨ Features

- 🚀 **SIMD-beschleunigt** – AVX2/NEON Distanzberechnung
- 🌲 **HNSW + IVF** – Zwei ANN-Indizes: HNSW (hoher Recall) und IVFFlat (RAM-effizient)
- 📁 **Datei-basiert** – Persistenz via WAL + B+Tree (Walhalla.Storage)
- 🔒 **Thread-safe** – ReaderWriterLockSlim, concurrent reads/writes
- 🧩 **Metadata-Filterung** – Suche mit `Func<Dictionary, bool>`
- 📸 **Snapshots** – Point-in-Time konsistente Queries
- 🌐 **Optional REST-API** – für Multi-User oder Remote-Zugriff
- 🎨 **Svelte-5 UI** – Management-Interface (optional)

---

## 🚀 Schnellstart

### Embedded (Standalone)

```csharp
using Walhalla.VectorStore;

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

### Mit REST-API + UI

```bash
# API starten
dotnet run --project Walhalla.VectorStore.Api

# UI starten
cd Walhalla.VectorStore.UI && npm run dev
```

- API: `http://localhost:5000`
- UI: `http://localhost:5173`
- Auth: `X-API-Key: walhalla-dev-key`

---

## 🏗️ Architektur

```
┌─────────────────────────────────────────┐
│  EmbeddedVectorStore (SQLite-like API) │
├─────────────────────────────────────────┤
│  VectorCollectionManager              │
│  ├── VectorCollection "documents"       │
│  │   ├── BlobStore (WAL + B+Tree)     │
│  │   ├── VectorCache (LRU)            │
│  │   ├── HnswIndex (ANN)              │
│  │   └── IvfFlatIndex (ANN)           │
│  └── VectorCollection "images"          │
├─────────────────────────────────────────┤
│  Walhalla.Storage.Blobs                 │
│  └── WalhallaStore (WAL + B+Tree)       │
└─────────────────────────────────────────┘
```

---

## 🧭 Index-Wahl: HNSW vs IVFFlat

Walhalla bietet **zwei ANN-Indizes** — je nach Anwendungsfall ist einer besser geeignet.

### Schnellentscheidung

```
Wenig RAM (< 50 MB Index) und < 100k Vektoren?
    └─ JA  → IVFFlat (RAM-effizient, schnell genug)
    └─ NEIN → HNSW (besserer Recall, skaliert beliebig)
```

### Vergleich

| Kriterium | HNSW | IVFFlat |
|---|---|---|
| **Recall** | 99–100% | 65–99% (abhängig von `nprobe`) |
| **RAM-Verbrauch** | Hoch (Graph im RAM) | Sehr niedrig (nur Centroids) |
| **Build-Zeit** | Langsamer | Schneller |
| **Suchzeit (10k)** | ~1 ms | ~0,0–0,4 ms |
| **Suchzeit (1Mio)** | ~2 ms | ~10–50 ms |
| **Inkrementelle Updates** | Nativ (Insert on-the-fly) | Nachträglich (Centroids veralten) |
| **Delete** | Markiert (Graph bleibt stabil) | Entfernt aus Cluster (leere Cluster möglich) |
| **Dimensionen** | Gut bis 1536+ | Ab 500+ schwieriger (Cluster unscharf) |
| **Konfiguration** | `M`, `ef` — einmalig | `nprobe`, `n_clusters` — tuning-intensiver |

### Empfohlene Szenarien

| Szenario | Index | Begründung |
|---|---|---|
| **IoT / Edge / Embedded** | **IVFFlat** | 128 KB–1 MB RAM, Flash-Preis zählt |
| **Desktop-App / Agent** | **HNSW** | Hoher Recall, inkrementell, kein Rebuild |
| **Server / >100k Vektoren** | **HNSW** | Logarithmische Suche, skaliert beliebig |
| **Hohe Dimensionen (1536D)** | **HNSW** | IVF braucht `nprobe=50+`, verliert Vorteil |
| **Batch-Import, statisch** | **IVFFlat** | Schneller Build, Query-Performance ausreichend |
| **Streaming / Online** | **HNSW** | Neue Vektoren sofort im Index, kein Rebuild |

### Code-Beispiele

#### HNSW (Default)
```csharp
var docs = store.GetOrCreateCollection(
    "documents", 1536, DistanceMetric.Cosine,
    enableHnsw: true,
    hnswOptions: new HnswOptions { M = 16, EfConstruction = 200, EfSearch = 128 });

var results = await docs.SearchAsync(new Vector(query), topK: 10);
```

#### IVFFlat (RAM-effizient)
```csharp
var docs = store.GetOrCreateCollection(
    "documents", 128, DistanceMetric.Euclidean,
    enableHnsw: false, enableIvf: true,
    ivfOptions: new IvfOptions { NClusters = 100, Nprobe = 5 });

await docs.RebuildIndexAsync(); // K-Means einmalig bauen
var results = await docs.SearchIvfAsync(new Vector(query), topK: 10, nprobe: 5)
    .ToListAsync();
```

#### Beide gleichzeitig (Fallback)
```csharp
var docs = store.GetOrCreateCollection(
    "documents", 512, DistanceMetric.Cosine,
    enableHnsw: true, enableIvf: true);

await docs.RebuildIndexAsync(); // Baut beide Indizes

// Schnelle IVF-Suche für grobe Ergebnisse
var approx = await docs.SearchIvfAsync(query, topK: 50, nprobe: 3).ToListAsync();

// Präzise HNSW-Suche für finale Top-10
var exact = await docs.SearchHnswAsync(query, topK: 10, ef: 128).ToListAsync();
```

### Parameter-Leitfaden

**HNSW:**
- `M = 16` — Standard, gut für die meisten Fälle
- `EfConstruction = 200` — höher = besserer Graph, langsamerer Build
- `EfSearch = 128` — höher = besserer Recall, langsamer

**IVFFlat:**
- `NClusters` — `0` (auto = sqrt(N)) für den Anfang, bei 100k Vektoren ~300
- `Nprobe` — `1` (schnellst, schlechtester Recall) bis `50` (langsamer, besserer Recall)
- `MaxIterations` — `20` Standard, `5` reicht für grobe Cluster

---

## 📦 NuGet

```bash
# Core Vector-Store
dotnet add package Walhalla.VectorStore

# gRPC-Client
dotnet add package Walhalla.VectorStore.Client

# Microsoft.Extensions.VectorData-Adapter
dotnet add package Walhalla.VectorStore.Microsoft.Extensions.VectorData

# Text-Embeddings (lokal via ONNX)
dotnet add package Walhalla.VectorStore.Embeddings.Onnx
```

**Lokaler Feed** (aus dem Repo bauen):
```bash
dotnet pack --configuration Release
# Pakete liegen in ./packages/
```

---

## 📂 Projektstruktur

| Projekt | Zweck | NuGet |
|---------|-------|-------|
| `Walhalla.VectorStore` | Core Library (SIMD, HNSW, Collections) | `Walhalla.VectorStore` |
| `Walhalla.VectorStore.Api` | ASP.NET Core REST + gRPC API | — |
| `Walhalla.VectorStore.UI` | Svelte 5 Management-Interface | — |
| `Walhalla.VectorStore.Client` | gRPC-Client | `Walhalla.VectorStore.Client` |
| `Walhalla.VectorStore.Microsoft.Extensions.VectorData` | Microsoft VectorData-Adapter | `Walhalla.VectorStore.Microsoft.Extensions.VectorData` |
| `Walhalla.VectorStore.Embeddings` | Text-Embedding-Abstraktionen | `Walhalla.VectorStore.Embeddings` |
| `Walhalla.VectorStore.Embeddings.Onnx` | Lokale ONNX-Embeddings | `Walhalla.VectorStore.Embeddings.Onnx` |
| `Walhalla.Indexes` | Index-Primitive (FullText, RTree, Bitmap) | `Walhalla.Indexes` |
| `Walhalla.Storage.Blobs` | Blob-Persistenz (WAL + B+Tree) | `Walhalla.Storage.Blobs` |
| `Walhalla.Storage` | Core Storage-Engine | `Walhalla.Storage` |

📖 **Vollständige Dokumentation:** Siehe [docs/](docs/)

---

## 🔧 Use Cases

- **Lokale AI-Agenten** – RAG ohne Cloud-Abhängigkeit
- **Desktop-Apps** – Semantische Suche in Dokumenten
- **Games** – NPC-Gedächtnis, Procedural Content
- **IoT/Edge** – Embedded Devices mit Vektor-Suche
- **Tests/Prototypen** – Kein Docker, kein Setup

---

## 📜 Lizenz

MIT License – siehe [LICENSE](LICENSE)

---

> *"Jeder Agent verdient ein Gedächtnis – auch ohne Internet."*
