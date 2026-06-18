# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Walhalla.VectorStore is an embedded vector store for .NET — file-based, zero-config, and standalone. It provides SIMD-accelerated vector search with HNSW and IVFFlat indexes, backed by a custom WAL + B+Tree storage engine. An optional ASP.NET Core REST API (with gRPC) and Svelte 5 management UI are included. Additional packages provide Microsoft.Extensions.VectorData integration and local ONNX embeddings.

## Build & Development Commands

### .NET Solution
- Build: `dotnet build`
- Run tests: `dotnet test`
- Run single test class: `dotnet test --filter "FullyQualifiedName~VectorDistanceTests"`
- Run API: `dotnet run --project Walhalla.VectorStore.Api`
- Run benchmarks: `dotnet run --project Walhalla.Benchmarks --configuration Release`

### Samples
- Embedded sample: `dotnet run --project samples/Sample.Embedded`
- HTTP client sample: `dotnet run --project samples/Sample.Http`
- gRPC client sample: `dotnet run --project samples/Sample.Grpc`
- Agent framework sample: `dotnet run --project samples/Sample.AgentFramework`

### UI (Svelte 5 + Vite)
- Dev server: `cd Walhalla.VectorStore.UI && npm run dev` (runs on `http://localhost:5173`)
- Build: `cd Walhalla.VectorStore.UI && npm run build`
- Type check: `cd Walhalla.VectorStore.UI && npm run check`

### Running the full stack
1. Start API: `dotnet run --project Walhalla.VectorStore.Api` → `http://localhost:5000`
2. Start UI: `cd Walhalla.VectorStore.UI && npm run dev` → `http://localhost:5173`
3. API expects header `X-API-Key: walhalla-dev-key`

## Solution Structure

| Project | Target | Purpose |
|---------|--------|---------|
| `Walhalla.Storage` | net8.0;net9.0;net10.0 | Core storage engine (WAL durability, persistent B+Tree) |
| `Walhalla.Storage.Blobs` | net8.0;net9.0;net10.0 | Blob sidecar on top of WalhallaStore — append-only file with WAL pointers |
| `Walhalla.Indexes` | net8.0;net9.0;net10.0 | Shared index primitives: FullTextIndex, RTree, PersistentRTree, PersistentFullTextIndex, SimpleBitmap |
| `Walhalla.VectorStore` | net8.0;net9.0;net10.0 | Core vector library (SIMD distances, HNSW/IVFFlat indexes, collections, filtering). `AllowUnsafeBlocks=true` |
| `Walhalla.VectorStore.Api` | net10.0 | ASP.NET Core minimal-API REST service + gRPC |
| `Walhalla.VectorStore.UI` | — | Svelte 5 + Vite + TypeScript management interface |
| `Walhalla.VectorStore.Tests` | net10.0 | xUnit tests |
| `Walhalla.VectorStore.Client` | net8.0;net9.0;net10.0 | gRPC client (Qdrant-like API). Proto: `Protos/walhalla.proto` |
| `Walhalla.VectorStore.Microsoft.Extensions.VectorData` | net8.0;net9.0;net10.0 | Adapter for `Microsoft.Extensions.VectorData.Abstractions` |
| `Walhalla.VectorStore.Embeddings` | net8.0;net9.0;net10.0 | Text-to-vector abstractions (no heavy native deps) |
| `Walhalla.VectorStore.Embeddings.Onnx` | net8.0;net9.0;net10.0 | Local ONNX embedding generator + tokenizer |
| `Walhalla.Benchmarks` | net10.0 | BenchmarkDotNet benchmarks |

## High-Level Architecture

### Storage Layer
- `WalhallaStore` (in `Walhalla.Storage`) implements a WAL-backed key-value store with a persistent B+Tree.
- `BlobStore` (in `Walhalla.Storage.Blobs`) stores large values in an append-only `blobs.dat` sidecar while keeping small 12-byte pointers in the WAL. This keeps the B+Tree small regardless of value size.
- Crash safety: blob payload is written with `FileOptions.WriteThrough` before the pointer is committed to the WAL. Compaction uses a two-phase commit with a sentinel key and atomic rename.

### Index Primitives (`Walhalla.Indexes`)
- `FullTextIndex` / `PersistentFullTextIndex` — inverted-index full-text search with `FullTextQueryMode.All` / `Any`.
- `RTree` / `PersistentRTree` — spatial index for bounding-box queries.
- `SimpleBitmap` — compressed bitmap primitive used internally.
- `PayloadIndex` — metadata field index used by `VectorCollection` for accelerating filtered queries.

### Vector Store Layer
- `EmbeddedVectorStore` is the top-level consumer API. It owns a `BlobStore` and a `VectorCollectionManager`.
- `VectorCollectionManager` manages multiple named `VectorCollection` instances in a single store.
- Each `VectorCollection` implements `IVectorRepository` and handles CRUD and search for one collection.
- Key layout in the underlying store:
  - `c:{name}:v:{id}` → vector bytes (Little-Endian float32)
  - `c:{name}:m:{id}` → metadata JSON
  - `c:{name}:s` → sequence number (for snapshot isolation)
  - `c:{name}:c` → persisted count
  - `c:{name}:chg:{seq:D20}` → change-feed event
- `VectorCache` is an LRU cache used to accelerate HNSW lookups.
- `PayloadIndex` accelerates metadata-filtered exact searches by indexing payload fields.
- Change feeds: `VectorCollection` exposes `ReadChangesAsync` for streaming collection mutations (used by the SSE endpoint).

### ANN Indexes
- `HnswIndex` (in `Walhalla.VectorStore.Indexes`) is an in-memory approximate nearest neighbor index. It stores nodes in RAM and loads vectors on-demand via a callback (`Func<ulong, float[]>`), typically hitting the `VectorCache` first.
- `IvfFlatIndex` (in `Walhalla.VectorStore.Indexes`) is a RAM-efficient ANN index using k-means clustering. Good for low-RAM / IoT scenarios.
- Deleted vectors are marked as deleted in the indexes but not immediately removed; both can be rebuilt via `RebuildIndexAsync`.
- HNSW supports async background indexing via `HnswOptions.AsyncIndexing` (uses a bounded channel + worker task).
- Search fallback hierarchy (extension method `SearchAsync`): HNSW → IVF → Exact.
- Cosine vectors are auto-normalized on insertion so that cosine similarity reduces to dot product at query time.

### SIMD Distance Calculations
- `VectorDistance` in `Walhalla.VectorStore` provides Euclidean, Cosine, and DotProduct distances.
- Uses AVX2/FMA when available (256-bit vectors, 8 floats per iteration), with scalar fallback.

### REST API
- Minimal API style using `MapGet` / `MapPost` / `MapDelete` in `Program.cs`.
- Endpoints grouped under `/api`:
  - `GET /api/collections`
  - `POST /api/collections`
  - `DELETE /api/collections/{name}`
  - `GET /api/collections/{name}/manifest`
  - `GET /api/collections/{name}/changes` (SSE stream)
  - `GET /api/collections/{name}/vectors`
  - `GET /api/collections/{name}/vectors/{id}`
  - `POST /api/collections/{name}/vectors`
  - `POST /api/collections/{name}/vectors/batch`
  - `DELETE /api/collections/{name}/vectors/{id}`
  - `POST /api/collections/{name}/search/exact`
  - `POST /api/collections/{name}/search/hnsw`
  - `POST /api/collections/{name}/search/ivf`
  - `POST /api/collections/{name}/search/text`
  - `POST /api/collections/{name}/search/hybrid`
  - `GET /api/stats`
  - `GET /health` / `GET /ready`
- API key middleware checks `X-API-Key` for all `/api` routes (skips OPTIONS for CORS).
- CORS is configured for `http://localhost:5173` and `http://localhost:4173`.
- `VectorStoreService` is a singleton that wraps the store lifecycle with `ReaderWriterLockSlim` for thread-safe collection mutations.

### gRPC
- Proto file lives in `Walhalla.VectorStore.Client/Protos/walhalla.proto`.
- Server service is registered in the API via `MapGrpcService<VectorStoreGrpcService>`.
- Interceptor `ApiKeyGrpcInterceptor` enforces the same API key for gRPC calls.

### UI
- Svelte 5 with runes (`$state`, `$effect`).
- `src/lib/api.ts` contains the `ApiClient` class that talks to the REST API.
- Components: `Dashboard`, `CollectionsPanel`, `SearchPanel`, `Toast`.

### Testing Patterns
- Tests use xUnit and create temporary `BlobStore` instances in `Path.GetTempPath()` with GUID-based directories.
- `VectorDistanceTests` verify SIMD correctness by comparing against manual scalar calculations across multiple dimensions (4, 8, 16, 32, 64, 128, 1536).
- `VectorCollectionTests` exercise the full collection lifecycle including batch inserts.
- Integration tests: `TransportIntegrationTests` (REST), `ClientTests` (gRPC client), `PersistentPayloadIndexIntegrationTests`.
- Dispose pattern: tests dispose `manager` then `store`, then delete the temp directory.

## Important Implementation Notes
- `AllowUnsafeBlocks` is enabled on `Walhalla.VectorStore` and `Walhalla.Benchmarks` for SIMD intrinsics.
- `VectorCollection.EnumerateIdsAsync` relies on `BlobStore.ScanPrefixAsync`, which performs a prefix scan over the store.
- `BlobVectorRepository` (in `VectorRepository.cs`) is an older/alternative implementation; the active path uses `VectorCollection` via `VectorCollectionManager`.
- The API project uses `ImplicitUsings=enable`; core libraries (`Walhalla.VectorStore`, `Walhalla.Indexes`, `Walhalla.Storage`, `Walhalla.Storage.Blobs`) use `ImplicitUsings=disable` with explicit usings.
- The UI project is a standard Vite + Svelte template; it does not use SvelteKit.
- `Walhalla.VectorStore.Embeddings` intentionally has no ONNX/Native dependency so lightweight nodes can reference it and plug in a remote generator. The concrete local ONNX implementation lives in `Walhalla.VectorStore.Embeddings.Onnx`.

## Language

Primary development language is German (code comments, READMEs, variable names in some places). Keep new comments and documentation in German to match existing style.
