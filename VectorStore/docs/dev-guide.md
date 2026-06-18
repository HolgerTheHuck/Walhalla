# Dev-Guide

Dieser Guide richtet sich an Entwickler, die am Walhalla-Projekt mitarbeiten oder die NuGet-Pakete lokal bauen und testen möchten.

---

## Voraussetzungen

- .NET SDK 8.0, 9.0 oder 10.0
- Node.js + npm (für UI)
- Windows, Linux oder macOS (Storage + VectorStore sind cross-platform; SIMD nutzt AVX2 auf x64 und scalar fallback auf ARM)

---

## Build

```bash
# Gesamte Solution
dotnet build

# Release-Build (für NuGet)
dotnet build --configuration Release
```

---

## Tests

```bash
# Alle Tests
dotnet test

# Einzelne Testklasse
dotnet test --filter "FullyQualifiedName~VectorDistanceTests"

# Mit Coverage
dotnet test --collect:"XPlat Code Coverage"
```

---

## Lokaler NuGet-Feed

Die Pakete landen zentral im Solution-Root unter `packages/` (konfiguriert via `Directory.Build.props`).

```bash
# Alle Pakete bauen
dotnet pack --configuration Release

# Feed registrieren (einmalig)
dotnet nuget add source D:\Develop\Own\VectorStore\packages --name walhalla-local

# Paket in Consumer-Projekt installieren
dotnet add package Walhalla.VectorStore --source walhalla-local
```

---

## UI entwickeln

```bash
cd Walhalla.VectorStore.UI
npm install   # falls noch nicht geschehen
npm run dev    # http://localhost:5173
npm run build
npm run check  # TypeScript + Svelte-Check
```

---

## API starten

```bash
dotnet run --project Walhalla.VectorStore.Api
```

- REST: `http://localhost:5000/api`
- gRPC: `http://localhost:5000`
- Swagger: `http://localhost:5000/swagger` (Development)
- Auth: `X-API-Key: walhalla-dev-key`

---

## Projektstruktur

```
Walhalla/
├── Walhalla.Storage/           # WAL + B+Tree Core
├── Walhalla.Storage.Blobs/     # Blob-Sidecar
├── Walhalla.Indexes/           # FullText, RTree, Bitmap
├── Walhalla.VectorStore/       # Core Vector Library (SIMD, HNSW, Collections)
├── Walhalla.VectorStore.Api/   # ASP.NET Core REST + gRPC
├── Walhalla.VectorStore.UI/    # Svelte 5 Management Interface
├── Walhalla.VectorStore.Client/ # gRPC Client
├── Walhalla.VectorStore.Microsoft.Extensions.VectorData/
├── Walhalla.VectorStore.Embeddings/
├── Walhalla.VectorStore.Embeddings.Onnx/
├── Walhalla.VectorStore.Tests/ # xUnit Tests
├── Walhalla.Benchmarks/        # BenchmarkDotNet
├── samples/                     # Beispiel-Apps
├── docs/                        # Dokumentation
└── packages/                    # Lokaler NuGet-Feed
```

---

## Test-Patterns

Tests verwenden temporäre Verzeichnisse in `Path.GetTempPath()`:

```csharp
var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
using var store = new BlobStore(new BlobStoreOptions(path));
using var manager = new VectorCollectionManager(store);

// ... test ...

// Cleanup
store.Dispose();
Directory.Delete(path, recursive: true);
```

**Wichtige Testklassen:**
- `VectorDistanceTests` – SIMD-Korrektheit gegen Scalar
- `VectorCollectionTests` – Collection-Lifecycle (CRUD, Batch, Suche)
- `HnswIndexTests` / `IvfFlatIndexTests` – Index-Korrektheit
- `TransportIntegrationTests` – REST-API-Roundtrips
- `ClientTests` – gRPC-Client-Roundtrips
- `PersistentPayloadIndexIntegrationTests` – PayloadIndex + Storage

---

## Wichtige Code-Konventionen

- **Sprache:** Kommentare und Doku auf Deutsch
- **ImplicitUsings:** Core-Libraries (`Walhalla.VectorStore`, `Walhalla.Storage`, `Walhalla.Indexes`) verwenden `ImplicitUsings=disable` mit expliziten Usings
- **UnsafeBlocks:** `AllowUnsafeBlocks=true` in `Walhalla.VectorStore` und `Walhalla.Benchmarks` für SIMD-Intrinsics
- **XML-Doku:** `GenerateDocumentationFile=true` in allen packbaren Projekten – existierende Kommentare landen in den NuGet-Paketen

---

## IntelliSense in NuGet-Paketen

Die XML-Dokumentation wird automatisch beim Packen in die `.nupkg`-Dateien eingebettet. Consumer sehen in IntelliSense die deutschen XML-Doc-Kommentare.

Solltest du ein Projekt lokal referenzieren (ProjectReference statt PackageReference), erscheint die Doku automatisch, sobald das referenzierte Projekt kompiliert ist.
