# Walhalla.VectorStore – Dokumentation

> **Der erste echte embedded Vector-Store für .NET.**
> Zero-config, datei-basiert, standalone. SIMD-beschleunigt, HNSW + IVFFlat, Metadata-Filterung, Volltextsuche, Snapshots.

---

## Paket-Übersicht

Walhalla ist modular aufgebaut. Installiere nur das, was du brauchst.

| Paket | Wofür? | NuGet |
|---|---|---|
| **Walhalla.VectorStore** | Embedded Vector-Store mit Collections, HNSW/IVF, Filter, Snapshots | `dotnet add package Walhalla.VectorStore` |
| **Walhalla.VectorStore.Client** | gRPC-Client für Remote-Zugriff auf einen laufenden Server | `dotnet add package Walhalla.VectorStore.Client` |
| **Walhalla.VectorStore.Microsoft.Extensions.VectorData** | Adapter für `Microsoft.Extensions.VectorData.Abstractions` | `dotnet add package Walhalla.VectorStore.Microsoft.Extensions.VectorData` |
| **Walhalla.VectorStore.Embeddings** | Abstraktionen für Text-Embeddings (keine schwere Abhängigkeit) | `dotnet add package Walhalla.VectorStore.Embeddings` |
| **Walhalla.VectorStore.Embeddings.Onnx** | Lokale ONNX-Embeddings + Tokenizer | `dotnet add package Walhalla.VectorStore.Embeddings.Onnx` |
| **Walhalla.Storage** | Low-Level WAL + B+Tree Key-Value Store | `dotnet add package Walhalla.Storage` |
| **Walhalla.Storage.Blobs** | Blob-Sidecar für große Werte auf Walhalla.Storage | `dotnet add package Walhalla.Storage.Blobs` |
| **Walhalla.Indexes** | Index-Primitive (FullText, RTree, Bitmap) | `dotnet add package Walhalla.Indexes` |

**Schnellentscheidung:**
- Nur lokal arbeiten? → `Walhalla.VectorStore`
- Mit Remote-Server sprechen? → `Walhalla.VectorStore.Client`
- Microsoft Semantic Kernel / VectorData nutzen? → `Walhalla.VectorStore.Microsoft.Extensions.VectorData`
- Text direkt in Vektoren umwandeln (lokal)? → `Walhalla.VectorStore` + `Walhalla.VectorStore.Embeddings.Onnx`

---

## Tutorials

| Tutorial | Beschreibung |
|---|---|
| [Embedded-Nutzung](embedded-usage.md) | `EmbeddedVectorStore` – Collections, CRUD, Suche, Filter, Snapshots |
| [gRPC-Client](client.md) | `WalhallaClient` – Verbindung, CRUD, Streaming, Change-Feed |
| [Microsoft.Extensions.VectorData](microsoft-extensions.md) | Integration in das Microsoft VectorData-Ökosystem |
| [Embeddings](embeddings.md) | Texte in Vektoren umwandeln (abstrakt + ONNX-lokal) |
| [Storage-Layer](storage.md) | Low-Level `WalhallaStore` und `BlobStore` direkt nutzen |

---

## API-Referenz

Eine kompakte Übersicht über alle öffentlichen Interfaces und wichtige Klassen findest du in der [API-Referenz](api-reference.md).

---

## Entwickler-Guide

Build, Test, lokaler NuGet-Feed und Projektstruktur für Contributors: [Dev-Guide](dev-guide.md)

---

## Sprache

Alle Dokumentation ist auf Deutsch gehalten (wie der Quellcode und die Kommentare).

## Lizenz

MIT – siehe [LICENSE](../LICENSE)
