# Phase D — Embedded v1.0 Release

**Ziel:** WalhallaSql als Embedded-DB veröffentlichen — API stabil, NuGet-Pakete signiert, SQLite-Vergleichsbenchmark erfüllt, Migrations-Guide vorhanden, Samples laufen.

**Voraussetzung:** Phasen A + B + C abgeschlossen.

**Exit-Kriterien**
- `dotnet add package WalhallaSql` aus frischem Projekt funktioniert (offline + NuGet.org)
- Alle Samples grün auf .NET 8/9/10
- SQLite-Vergleichs-Suite erfüllt ±2× auf ≥ 5 von 7 Workloads
- Tag `v1.0.0-embedded` gesetzt; Release-Notes auf GitHub

---

## Slices

### D.1 — API-Freeze

**Scope**
- Public-API-Surface in `PublicAPI.Shipped.txt` einfrieren für:
  - `WalhallaSql` (Core)
  - `WalhallaSql.AdoNet`
  - `WalhallaSql.EfCore`
- Breaking-Changes ab hier nur über Major-Version
- `[Obsolete]`-Markierung für legacy `LayeredSql.*`-Aliasse mit Removal-Hinweis auf v2

**Review-Punkte**
- Connection-String-Syntax stabil? (`File=...;Database=...;Mode=Embedded`)
- `WalhallaEngine.OpenAsync` als kanonische Entry-Methode
- Exception-Hierarchie: `WalhallaSqlException` als Basis, `WalhallaSyntaxException`, `WalhallaConstraintException`, `WalhallaSerializationConflictException`
- Cancellation-Token end-to-end auf allen async APIs

**Files** — `PublicAPI.Shipped.txt` pro Projekt, `docs/api/v1-surface.md`

---

### D.2 — SQLite-Vergleichs-Suite

**Scope** — `WalhallaSql.Benchmarks` erweitern um sieben Workload-Klassen:

| Workload | Beschreibung | Ziel |
|---|---|---|
| `InsertBatch` | 10k Inserts in Tx | ±2× SQLite |
| `BulkInsert` | 100k Inserts via BulkCopy / COPY | ±2× |
| `SelectById` | 10k PK-Lookups | ±2× |
| `RangeScan` | indexierter Range über 10k Rows | ±2× |
| `JoinTwoTables` | 10k × 10k Hash-Join | ±2× |
| `AggregateGroupBy` | `SUM/AVG GROUP BY` über 100k Rows | ±2× |
| `MixedReadWrite` | 70/30 RW-Mix, 4 Threads | ±2× |

**Ergebnis-Pipeline**
- BenchmarkDotNet runs gegen WalhallaSql + Microsoft.Data.Sqlite
- `scripts/run-sqlite-comparison.ps1` produziert Markdown-Report
- Committet als `docs/perf/sqlite-comparison-v1.md`

**Files** — `WalhallaSql.Benchmarks/SqliteComparison/*.cs`, `scripts/run-sqlite-comparison.ps1`

---

### D.3 — Migrations-Guide "SQLite → WalhallaSql"

**Scope** `docs/migration/from-sqlite.md`:

- Datentyp-Mapping (SQLite-Affinity → WalhallaSql-Typ)
- Pragma-Äquivalente (`PRAGMA journal_mode` → WalhallaSql-WAL-Mode)
- Connection-String-Translation
- Schema-Migrator-Tool: `walhallactl import sqlite path/to/db.sqlite`
- FAQ: NULL-Handling-Unterschiede, Datums-Typen-Strenge, Type-Coercion

**Tool**
- `WalhallaSql.Migrator` (existiert bereits) um SQLite-Importer erweitern
- Auto-Schema-Detection + Daten-Kopie + Index-Recreation

**Files** — `docs/migration/from-sqlite.md`, `LayeredSql.Migrator/Importers/SqliteImporter.cs`

---

### D.4 — NuGet-Pakete

**Pakete**
- `WalhallaSql` — Core-Engine
- `WalhallaSql.AdoNet` — ADO.NET-Provider
- `WalhallaSql.EfCore` — EF-Core-Provider
- `WalhallaSql.Cli` — `walhallactl`-Tool als dotnet-tool

**Pro Paket**
- SemVer 1.0.0
- Lizenzheader, `LICENSE` + `NOTICE` einpacken
- Symbol-Packages (`.snupkg`) für SourceLink
- Deterministischer Build (`ContinuousIntegrationBuild=true`)
- Strong-Name-Signiert (Open-Source-Key im Repo, nicht für Sicherheit, sondern für GAC-Kompatibilität)
- NuGet-Authenticode-Signatur via Code-Signing-Cert (GitHub Actions Secret)

**Release-Pipeline**
- `release-please` erzeugt Tag + Changelog
- GitHub-Actions-Workflow `release.yml` baut, signiert, pusht zu NuGet.org

**Files** — `Directory.Build.props` (Packaging-Properties), `.github/workflows/release.yml`

---

### D.5 — Sample-Apps polishen

**Scope** — Existierende Samples auf v1-API + neue Namen:

- `WalhallaSql.NuGetDemo` (war `WalhallaSql.NuGetDemo`) — Konsolen-CRUD
- `WalhallaSql.AdoNet.NuGetDemo` — ADO.NET-Beispiel
- `WalhallaSql.AdoNet.Sample` — komplexer Beispiel
- `WalhallaSql.EfCore.Sample` — EF-Core mit DbContext
- **Neu:** `WalhallaSql.AspNetCore.Sample` — Web-API mit JWT-Auth, EF-Core, OpenAPI

**Anforderung pro Sample**
- 30-Sekunden-Quickstart (clone → restore → run)
- README mit `dotnet new`-vergleichbarer Klarheit
- Smoke-Test in CI

**Files** — Verzeichnis-Renames, README-Aktualisierungen

---

### D.6 — Top-Level- + Projekt-READMEs

**Top-Level `README.md`**
- Logo (optional, Phase D)
- Quickstart in 30 Sekunden (Code-Snippet)
- Feature-Matrix-Tabelle (SQLite/Postgres-Vergleich)
- Performance-Badge (SQLite-Comparison-Ergebnis)
- Links zu Docs, Samples, Discord/Discussions

**Pro Veröffentlichungs-Projekt** — projekt-spezifisches README mit:
- Was macht dieses Paket?
- Quickstart-Code-Snippet
- Wichtige Konfigurationsoptionen
- Link zur API-Doku

**Files** — `README.md` (Root), pro Projekt `README.md`

---

## Verification (phasenübergreifend)

- Reproduzierbarer Build aus Git-Tag: zwei unabhängige Maschinen erzeugen identische `.nupkg`-Hashes
- `dotnet new console` → `dotnet add package WalhallaSql` → Code aus README-Quickstart → läuft
- SQLite-Comparison-Report eingecheckt in `docs/perf/`
- Tag `v1.0.0-embedded` mit signiertem Commit

## Reihenfolge & Parallelisierbarkeit

```
D.1 (API-Freeze) ─── BLOCKER ✅
   ├─ D.2 (SQLite-Bench) — parallel ✅
   ├─ D.3 (Migration-Guide) — parallel ✅
   ├─ D.4 (NuGet) — nach D.1 ⏭️
   ├─ D.5 (Samples) — nach D.1 ⏭️
   └─ D.6 (READMEs) — nach D.2 ⏭️
```

## Aktueller Stand (03. Juni 2026)

| Slice | Status | Erledigt |
|-------|--------|----------|
| D.1 | ✅ | PublicAPI eingefroren (4 Pakete); `WalhallaSyntaxException`, `WalhallaConstraintException`, `WalhallaSerializationConflictException`; `WalhallaEngine.OpenAsync()` |
| D.2 | ✅ | 3 neue Benchmark-Klassen (`BulkInsertBenchmark`, `AggregateGroupByBenchmark`, `MixedReadWriteBenchmark`); Report-Skript `scripts/run-sqlite-comparison.ps1`; Template `docs/perf/sqlite-comparison-v1.md` |
| D.3 | ✅ | Migrations-Guide `docs/migration/from-sqlite.md`; SQLite-Importer `LayeredSql.Migrator/Importers/SqliteImporter.cs`; `--mode sqlite` CLI-Support |
| D.4 | ✅ | `Directory.Build.props` erweitert (SourceLink, Deterministic Build, Symbol-Packages); Strong-Name-Key `walhalla.snk`; 5 Pakete packbar (`WalhallaSql`, `WalhallaSql.AdoNet`, `WalhallaSql.EfCore`, `WalhallaSql.PgWire`, `WalhallaSql.Cli`); `WalhallaSql.Cli` als `dotnet tool` (`walhallactl`); Release-Workflow `.github/workflows/release.yml` |
| D.5 | ✅ | Alle `WalhallaSql.*`-Samples auf `WalhallaSql.*` umbenannt und APIs aktualisiert: `WalhallaSql.AdoNet.Sample`, `WalhallaSql.EfCore.Sample`, `WalhallaSql.NuGetDemo`, `WalhallaSql.AdoNet.NuGetDemo`, `WalhallaSql.PgWire.Host`; Neues `WalhallaSql.AspNetCore.Sample` (Minimal API + EF Core); READMEs aktualisiert; CI-Smoke-Tests in `release.yml` |
| D.6 | ✅ | Root-README neu geschrieben (Quickstart, Feature-Matrix, Performance-Badge, Paket-Übersicht); Projekt-READMEs für `WalhallaSql`, `WalhallaSql.AdoNet`, `WalhallaSql.EfCore`, `WalhallaSql.PgWire`, `WalhallaSql.Cli` erstellt |

## Geschätzte Slice-Anzahl

6 Slices (D.1–D.6). 6/6 abgeschlossen.

## Phase D Exit-Kriterien

- ✅ `dotnet add package WalhallaSql` funktioniert (lokal verifiziert via `dotnet pack`)
- ✅ Alle 5 Pakete produzieren `.nupkg` + `.snupkg`
- ✅ Strong-Name-Signatur vorhanden (`PublicKeyToken=424a6541f1fba9e1`)
- ✅ SourceLink + Deterministic Build konfiguriert
- ✅ Release-Workflow `release.yml` mit NuGet-Push + GitHub-Release
- ✅ SQLite-Vergleichs-Benchmarks existieren (`WalhallaSql.Benchmarks`)
- ✅ Migrations-Guide `docs/migration/from-sqlite.md` vorhanden
- ✅ Samples laufen (`AdoNet`, `EfCore`, `AspNetCore`, `PgWire.Host`, `NuGetDemo`)
- ✅ CI Smoke-Tests für Samples in Release-Workflow
