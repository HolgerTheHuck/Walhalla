# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

This repository contains two related .NET products:

- **Walhalla.VectorStore** (`VectorStore/`) — An embedded vector store for .NET (HNSW/IVFFlat ANN indexes, SIMD distances, metadata filtering, optional REST/gRPC API and Svelte UI).
- **WalhallaSql** (`WalhallaSql/`) — An embedded SQL database engine (parser, planner, execution runtime, ADO.NET/EF Core providers, PgWire server).

Both share the same storage foundation in `Walhalla.Storage`. The repo is **not a Git repository**; there is no `git checkout` fallback, so back up important changes before destructive edits.

> **Current convergence state:** The M6 "Storage Convergence" migration is partially complete. `WTreeModern` source files still exist under `WalhallaSql/WTreeModern/` but are no longer referenced by any solution or project. `MvccBPlusTree` in `Walhalla.Storage` is the active MVCC storage backend. See `M6-STATUS.md` for the paused/interrupted details.

## Build & Development Commands

All commands assume you run them from the repository root `E:\Develop\WalhallaProject`.

### .NET

The repo uses the .NET 10 preview SDK and multi-targets libraries to `net8.0;net9.0;net10.0`. Executables/tests generally target `net10.0`.

| Task | Command |
|------|---------|
| Build entire solution | `dotnet build` |
| Build only the SQL stack | `dotnet build WalhallaSql/WalhallaSql.sln` |
| Run all tests in the root solution | `dotnet test` |
| Run one test class | `dotnet test <project> --filter "FullyQualifiedName~<ClassName>"` |
| Run VectorStore distance tests | `dotnet test VectorStore/Walhalla.VectorStore.Tests --filter "FullyQualifiedName~VectorDistanceTests"` |
| Run WalhallaSql core tests | `dotnet test WalhallaSql/WalhallaSql.Tests` |
| Run PgWire tests | `dotnet test WalhallaSql/WalhallaSql.PgWire.Tests` |

### Applications

| Application | Command |
|-------------|---------|
| VectorStore REST/gRPC API | `dotnet run --project VectorStore/Walhalla.VectorStore.Api` (default key: `X-API-Key: walhalla-dev-key`) |
| VectorStore UI (Svelte 5 + Vite) | `cd VectorStore/Walhalla.VectorStore.UI && npm run dev` |
| WalhallaSql migration CLI | `dotnet run --project WalhallaSql/WalhallaSql.Cli -- <args>` |
| PgWire host | `dotnet run --project WalhallaSql/WalhallaSql.PgWire.Host -- --path ./data --port 5432` |
| VectorStore benchmarks | `dotnet run --project VectorStore/Walhalla.Benchmarks --configuration Release` |
| WalhallaSql benchmarks | `dotnet run --project WalhallaSql/WalhallaSql.Benchmarks --configuration Release` |

### Packaging

- `dotnet pack --configuration Release` — produces NuGet packages; the VectorStore README states packages land under `./packages/`.

## Solution Structure

### VectorStore (`VectorStore/`)

| Project | Target | Purpose |
|---------|--------|---------|
| `Walhalla.Storage` | net8/9/10 | Core storage engine (`IKeyValueStore`, `MvccBPlusTree`, ODS pager, WAL) |
| `Walhalla.Storage.Blobs` | net8/9/10 | Blob sidecar for the legacy `BPlusTree` backend |
| `Walhalla.Indexes` | net8/9/10 | Shared index primitives (FullText, RTree, Bitmap, PayloadIndex) |
| `Walhalla.VectorStore` | net8/9/10 | Core vector library (SIMD, HNSW/IVF, collections); `AllowUnsafeBlocks=true` |
| `Walhalla.VectorStore.Api` | net10 | ASP.NET Core REST + gRPC server |
| `Walhalla.VectorStore.UI` | — | Svelte 5 + Vite management UI |
| `Walhalla.VectorStore.Tests` | net10 | xUnit tests |
| `Walhalla.VectorStore.Client` | net8/9/10 | gRPC client |
| `Walhalla.VectorStore.Microsoft.Extensions.VectorData` | net8/9/10 | `Microsoft.Extensions.VectorData.Abstractions` adapter |
| `Walhalla.VectorStore.Embeddings` / `.Onnx` | net8/9/10 | Text embedding abstractions and local ONNX implementation |
| `Walhalla.Benchmarks` | net10 | BenchmarkDotNet benchmarks |

See `VectorStore/CLAUDE.md` for deeper VectorStore-specific guidance. Note that it still mentions the legacy `WTreeStore` path; the current default is `MvccBPlusTree`.

### WalhallaSql (`WalhallaSql/`)

| Project | Target | Purpose |
|---------|--------|---------|
| `WalhallaSql` | net8/9/10 | Core SQL engine; strong-named (`walhalla.snk`) |
| `WalhallaSql.AdoNet` | net8/9/10 | ADO.NET provider |
| `WalhallaSql.EfCore` | net8/9/10 | EF Core provider |
| `WalhallaSql.PgWire.Abstractions` | net8/9/10 | PgWire protocol abstractions |
| `WalhallaSql.PgWire` | net8/9/10 | PostgreSQL wire-protocol server |
| `WalhallaSql.PgWire.Host` | net8/9/10 | Standalone PgWire host executable |
| `WalhallaSql.Cli` | net8/9/10 | `walhallactl` migration/utility tool |
| `WalhallaSql.Tests` | net10 | Core engine xUnit tests |
| `WalhallaSql.PgWire.Tests` | net10 | PgWire xUnit tests |
| `WalhallaSql.CrashTests` / `.CrashWorker` | net10 | Crash-recovery property tests |
| `WalhallaSql.Benchmarks` | net8/9/10 | BenchmarkDotNet benchmarks |

## High-Level Architecture

### Shared Storage Layer (`Walhalla.Storage`)

- The common contract is `IKeyValueStore` (`Walhalla.Storage.Contract`).
- **`MvccBPlusTree`** (default) is the active MVCC-aware B+Tree. It lives in `VectorStore/Walhalla.Storage/Trees/MvccBPlusTree.cs` and is exposed as `IKeyValueStore` through `MvccBPlusTreeStore`.
- **Legacy `BPlusTree`** (`WalhallaSql/WalhallaSql/Storage/BPlusTree.cs`) plus `BlobStore`/`Walhalla.Storage.Blobs` remains usable via `StorageBackend.BPlusTree` / `StorageMode.BPlusTree`.
- Storage layout: ODS paged data file + optional WAL log file; overflow values larger than the configured threshold are stored out-of-line.
- `Walhalla.Storage` is used by both `Walhalla.VectorStore` (via `MvccBPlusTreeStore`) and `WalhallaSql` (via `TableStore` selecting the configured `StorageMode`).

### WalhallaSql Architecture

- **`WalhallaEngine`** (`WalhallaSql/Api/WalhallaEngine.cs`) is the top-level API. It owns a `TableStore` and uses `WalhallaOptions` (including `StorageMode`).
- **Storage backend selection** happens in `TableStore`. `StorageMode.MvccBPlusTree` is the default and gives full MVCC snapshot isolation; `StorageMode.BPlusTree` is the legacy on-disk tree (kept for compatibility); `StorageMode.InMemory` uses an ephemeral store.
- **SQL pipeline:** parser → binder/catalog → query planner (cost-based with statistics) → execution engine (joins, window functions, CTEs, etc.).
- **Concurrency:** cross-process file lock via `wal.lock`; MVCC uses sequence numbers and a transaction manager in `Walhalla.Storage.Mvcc.Transactions`.
- **Client surfaces:** `WalhallaSql.AdoNet` provider, `WalhallaSql.EfCore` provider, and `WalhallaSql.PgWire` PostgreSQL wire-protocol server.

### VectorStore Architecture

- **`EmbeddedVectorStore`** is the consumer entry point. It creates a `StorageEngineOptions`-backed `IKeyValueStore` (default `MvccBPlusTree`) and a `VectorCollectionManager`.
- **Collection layout** in the key-value store uses prefixed keys: `c:{name}:v:{id}` for vectors, `c:{name}:m:{id}` for metadata JSON, `c:{name}:s` for sequence numbers, `c:{name}:chg:{seq}` for change-feed events.
- **Indexes:** `HnswIndex` (in-memory graph) and `IvfFlatIndex` (k-means clusters) in `Walhalla.VectorStore.Indexes`. Search falls back HNSW → IVF → exact scan.
- **Supporting pieces:** `VectorCache` (LRU), `PayloadIndex` (metadata-filtered exact search), SIMD `VectorDistance` (AVX2/FMA with scalar fallback).
- **Server surfaces:** ASP.NET Core minimal API (`/api/...`) plus gRPC; Svelte 5 UI talks to the REST API.

## Code Conventions

- **Language:** Primary development language for code comments, variable names, and documentation is **German**. Keep new comments and docs in German to match existing style.
- **Implicit usings:** Core libraries (`WalhallaSql`, `Walhalla.Storage`, `Walhalla.VectorStore`, etc.) use `ImplicitUsings=disable` with explicit usings. API/tests use `ImplicitUsings=enable`.
- **Unsafe code:** `AllowUnsafeBlocks=true` on `Walhalla.VectorStore` and benchmarks for SIMD intrinsics.
- **Strong naming:** `WalhallaSql` assemblies are signed with `walhalla.snk`; `InternalsVisibleTo` attributes include public keys for test/adjacent projects.
- **Public API tracking:** `WalhallaSql` uses `Microsoft.CodeAnalysis.PublicApiAnalyzers` with `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`. The current build emits many `RS0025` duplicate-symbol warnings; these are from the analyzer files, not code defects.

## Notes for Future Work

- The M6 storage convergence left `WalhallaSql/WTreeModern/` as dead code. Before deleting it, verify no remaining project references and back up the directory (the user has confirmed backups exist).
- The `VectorStore/CLAUDE.md` file contains detailed VectorStore guidance but predates the MvccBPlusTree default. Treat it as a useful reference and update it when storage/backend details change.
- The repository root solution `Walhalla.sln` includes both VectorStore and WalhallaSql projects. The dedicated `WalhallaSql/WalhallaSql.sln` contains only the SQL stack.
