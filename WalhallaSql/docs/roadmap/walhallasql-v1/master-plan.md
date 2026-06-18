# Master-Plan: WalhallaSql Road to v1.0

> Detaillierter Sub-Plan pro Phase: siehe [README.md](README.md).

## Vision

WalhallaSql wird als feature-vollständige SQL-DB veröffentlicht: **Embedded zuerst (SQLite-Klasse), C/S nachgezogen (Postgres-Klasse via PG-Wire)**. v1 erzwingt MVCC, SCRAM-Auth, Backup/Restore, logische Replikation, Observability, sowie die SQL-Lücken Window Functions, CHECK-Constraints, mehr Join-Algorithmen, GIN/GIST-Indizes, echte ICU-Collations und Postgres-JSON-Operatoren. Lizenz: OSS, Distribution via NuGet + standalone Server-Binary. Vorgehen: strikt inkrementelle Slices, jeder Slice mit grünem Build + Tests + Benchmark-Delta. Ziele: SQLite ±2× auf embedded Mixed-Workload, Postgres ±2× auf TPC-H Skala 1 über PG-Wire.

## Roadmap-Übersicht

| Phase | Thema | Ergebnis |
|---|---|---|
| **0** | Slice-Aufräumarbeit (laufend) | Slice 2 (EF-README), 4 (Storage-Kopie), 5 (Engine-Split), 6-LoadTests abschließen |
| **A** | Foundation Hardening | Tests baseline grün, CI, Build-Reproduzierbarkeit, Lizenz, Repo-Struktur |
| **B** | SQL-Engine-Vollständigkeit | CHECK, Window Functions, Join-Algorithmen, JSON-Operatoren, Recursive CTEs |
| **C** | Storage & Concurrency | MVCC/Snapshot-Isolation, Crash-Recovery-Hardening, ICU-Collations, GIN/GIST |
| **D** | Embedded v1.0 Release | API-Freeze, NuGet-Pakete, SQLite-Vergleichs-Suite, Migrationsguide |
| **E** | C/S Server Hardening | PG-Wire Auth/TLS/COPY, Rollen+GRANTs, Connection Pooling, Online-Backup |
| **F** | Replikation & Ops | Logical Replication, OpenTelemetry, pg_stat_statements-Äquivalent, Admin-CLI |
| **G** | C/S v1.0 Release | TPC-H-Benchmark, Container-Distribution, Production-Guide |
| **H** | Large-Object-/Blob-Sidecar | Per-Spalte-BLOB-Auslagerung in append-only Sidecar (`blobs.dat`), MVCC-Ref-Sharing, VACUUM-Compaction |

## Querschnittsthemen (laufend, nicht in einer Phase)

- **Dokumentation** — jeder Feature-Slice ergänzt `docs/`. Eigene Webseite (`mkdocs-material` o.ä.) ab Phase D.
- **Sicherheit** — Security-Audit vor v1.0; `SECURITY.md` ab Phase A; CVE-Process; Fuzzing (`SharpFuzz`) auf SQL-Parser + PG-Wire-Decoder.
- **Telemetrie-Disziplin** — kein neues Feature ohne Trace-Token + Telemetrie-Counter (Pattern: vorhandenes `WalhallaSql.Diagnostics.*`).
- **Bench-Disziplin** — jeder Engine-Slice mit BenchmarkDotNet-Vorher/Nachher in `BenchmarkSuite1/` + Snapshot in `docs/perf/`.

## Risiken & Entscheidungen

1. **MVCC vs. Locking** — MVCC ist die größte Einzelinvestition (Phase C.2). Risiko: Performance-Regression auf Schreibpfad. Gegenmaßnahme: parallel altes Locking als opt-in halten (C.2.5).
2. **Wire-Kompatibilität vs. eigene Wahrheit** — PG-Wire-Treue erzwingt Postgres-Semantik (NULL-Handling, Typ-Casts, SQLSTATE-Codes). Wo Konflikt mit aktueller WalhallaSql-Semantik: Postgres gewinnt. Differential-Tests sichern das ab.
3. **Lizenz** — `Apache 2.0` empfohlen — Patent-Grant ist für DB-Engine relevant.
4. **Naming** — `WalhallaSql` durchgängig. Branding/Logo später (Phase D).
5. **Out-of-Scope für v1** — Distributed/Sharded-Setup, Time-Series-Spezialfeatures, Vector-Search, GraphQL-Frontend.

## Aktueller Stand (03. Juni 2026)

- HEAD auf `feature/walhallasql`.
- **Phase A** ✅ abgeschlossen (Foundation Hardening).
- **Phase B** ✅ abgeschlossen (B.1–B.7: CHECK, Window, Joins, JSON, Recursive CTE, UPSERT/MERGE, Plan-Cache). 353/353 Tests grün (net8.0/9.0/10.0).
- **Phase C** ✅ abgeschlossen — C.0–C.5 + C.7 vollständig; 485 Tests grün (net8.0/9.0/10.0)
  - C.0 (Thread-Safety) ✅, C.1 (MVCC-Design) ✅, C.2.1–C.2.5 (SQL-Bridge) ✅
  - C.3 (Crash-Recovery) ✅, C.4 (ICU-Collations) ✅, C.5 (GIN-Indizes) ✅
  - C.6 ❌ (v1.x-Backlog)
  - **C.7 (Statistiken & Planner)** ✅ — C.7.1–C.7.9 vollständig · 485 Tests grün · Telemetrie + Bench + Doku ✅
- **Phase D** ✅ abgeschlossen (Embedded v1.0 Release)
  - D.1 (API-Freeze) ✅ — PublicAPI eingefroren, Exception-Hierarchie erweitert, OpenAsync hinzugefügt
  - D.2 (SQLite-Vergleichs-Suite) ✅ — BulkInsert, AggregateGroupBy, MixedReadWrite Benchmarks + Report-Skript
  - D.3 (Migrations-Guide & SQLite-Importer) ✅ — `docs/migration/from-sqlite.md`, `SqliteImporter.cs`, `--mode sqlite` in Migrator
  - D.4 (NuGet-Pakete) ✅ — NuGet-Spezifikationen erstellt, Paket-Build-Skripte in CI
  - D.5 (Samples) ✅ — `samples/embedded/` und `samples/server/` mit EF-Core + Dapper
  - D.6 (READMEs) ✅ — `README.md` + `WalhallaSql/README.md` + `WalhallaSql.PgWire/README.md`
- **Phase E** 🔄 in progress (C/S Server Hardening)
  - E.1 (SCRAM-SHA-256) ✅ — `ScramSha256Server` + `AuthIdCatalog`, `psql` verbindet mit Auth
  - E.2 (TLS) ✅ — `SSLRequest`-Handshake, PEM/X.509-Store-Laden, `openssl s_client` grün
  - E.3 (COPY Protocol) ✅ — `COPY FROM/TO STDIN/STDOUT` TEXT/CSV, Npgsql-Tests 7/7 grün
  - E.4 (Rollen + GRANTs) ⏭️ pending
  - E.5 (Connection Pooling) ⏭️ pending
  - E.6 (Online-Backup) ⏭️ pending
  - E.7 (WebSocket-Tunnel) ⏭️ pending
- **Phase H** ✅ abgeschlossen (Large-Object-/Blob-Sidecar)
  - H.1 (BlobRef + RowCodec) ✅
  - H.2 (Sidecar-Datei-Engine) ✅
  - H.3 (Write/Offload) ✅
  - H.4 (Read/Resolve) ✅
  - H.5 (MVCC-Version-Chain-Ref-Sharing) ✅
  - H.6 (Crash-Recovery) ✅
  - H.7 (Compaction/VACUUM) ✅
  - H.8 (DDL-Lebenszyklus) ✅
  - H.9 (Config, Telemetrie, Benchmarks, Doku) ✅
