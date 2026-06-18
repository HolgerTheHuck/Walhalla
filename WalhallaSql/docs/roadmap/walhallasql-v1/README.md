# WalhallaSql Road to v1.0

Sammlung aller Roadmap-Dokumente für die Produktreife von WalhallaSql als feature-vollständige SQL-Datenbank.

## Eckdaten

- **Use-Case-Reihenfolge:** Embedded zuerst (SQLite-Klasse) → C/S nachgezogen (Postgres-Klasse via PG-Wire)
- **Concurrency:** MVCC / Snapshot-Isolation als v1-MUSS
- **Wire:** Postgres-Wire mit SCRAM-SHA-256, TLS, COPY; WebSocket-Tunnel als offizielle Variante
- **Lizenz:** OSS (MIT oder Apache 2.0), NuGet + standalone Server-Binary
- **Vorgehen:** strikt inkrementelle Slices, jeder Slice grün (Build + Tests + Bench-Delta)
- **Performance-Ziele:** SQLite ±2× (Embedded Mixed-Workload), Postgres ±2× (TPC-H Skala 1 über PG-Wire)

## Phasen

| Phase | Datei | Thema |
|---|---|---|
| **Master** | [master-plan.md](master-plan.md) | Gesamtübersicht, Risiken, Querschnittsthemen |
| **0** | *(in-flight, nicht als Sub-Plan)* | Aufräumarbeit: Slice 2 (EF-README), 4 (Storage-Kopie), 5 (Engine-Split), 6-LoadTests |
| **A** | [phase-A-foundation.md](phase-A-foundation.md) | Foundation Hardening — Baseline grün, CI, Lizenz, Repo-Struktur |
| **B** | [phase-B-sql-engine.md](phase-B-sql-engine.md) | SQL-Engine-Vollständigkeit — CHECK, Window, Joins, JSON, CTE, UPSERT |
| **C** | [phase-C-storage-mvcc.md](phase-C-storage-mvcc.md) | Storage & Concurrency — Thread-Safety-Baseline (C.0), MVCC, ICU-Collations, GIN/GIST, ANALYZE ([C.7-Detailplan](phase-C7-statistics-detail.md)) |
| **D** | [phase-D-embedded-release.md](phase-D-embedded-release.md) | Embedded v1.0 Release — API-Freeze, SQLite-Vergleich, NuGet |
| **E** | [phase-E-server-hardening.md](phase-E-server-hardening.md) | C/S Server Hardening — SCRAM, TLS, COPY, Rollen, Backup |
| **F** | [phase-F-replication-ops.md](phase-F-replication-ops.md) | Replikation & Ops — Logical Replication, OTel, CLI |
| **G** | [phase-G-cs-release.md](phase-G-cs-release.md) | C/S v1.0 Release — TPC-H, Container, 72h Soak |
| **H** | [phase-H-blob-sidecar.md](phase-H-blob-sidecar.md) | Large-Object-/Blob-Sidecar — per-Spalte-BLOB-Auslagerung, MVCC-fest, Compaction |

## Querschnittsthemen (laufend)

- **Doku** — jeder Feature-Slice ergänzt `docs/`; eigene Webseite ab Phase D
- **Sicherheit** — Audit vor v1.0, `SECURITY.md` ab Phase A, Fuzzing auf SQL-Parser + PG-Wire-Decoder
- **Telemetrie-Disziplin** — kein neues Feature ohne Trace-Token + Counter (Pattern: `WalhallaSql.Diagnostics.*`)
- **Bench-Disziplin** — jeder Engine-Slice mit BenchmarkDotNet-Vorher/Nachher in `BenchmarkSuite1/`, Snapshot in `docs/perf/`

## Out-of-Scope für v1

Distributed/Sharded-Setup · Time-Series-Spezialfeatures · Vector-Search · GraphQL-Frontend

## Aktueller Stand (03. Juni 2026)

- HEAD auf `feature/walhallasql` — Phase E in progress
- **Phase A–D** ✅ abgeschlossen — 485 Tests grün, API-Freeze, NuGet-Spezifikationen, SQLite-Vergleichs-Suite, Migrations-Guide
- **Phase E** 🔄 in progress
  - E.1 (SCRAM-SHA-256) ✅ — `psql` / Npgsql mit Auth
  - E.2 (TLS) ✅ — `SSLRequest`-Handshake, PEM + X.509-Store, `openssl s_client` grün
  - E.3 (COPY Protocol) ✅ — `COPY FROM/TO STDIN/STDOUT` TEXT/CSV, Npgsql-Tests 7/7 grün
  - E.4–E.7 ⏭️ pending (Rollen + GRANTs, Connection Pooling, Online-Backup, WebSocket-Tunnel)
- **Phase H** ✅ abgeschlossen (Large-Object-/Blob-Sidecar)
  - H.1 (BlobRef + RowCodec) ✅ — Sentinel-Format, backward-compatible
  - H.2 (Sidecar-Datei-Engine) ✅ — Append, WriteThrough, MMAP, Compaction-Support
  - H.3 (Write/Offload) ✅ — EncodeRowWithBlobs in allen Write-Pfaden
  - H.4 (Read/Resolve) ✅ — DecodeRowWithBlobs mit PendingBlobValue-Streaming
  - H.5 (MVCC-Version-Chain-Ref-Sharing) ✅ — BlobRef-Wiederverwendung bei unverändertem Blob, Prune-Callback für Orphan-Tracking
  - H.6 (Crash-Recovery) ✅ — CrashWorker --blob, Blob-Crash-Tests, Property-Tests
  - H.7 (Compaction/VACUUM) ✅ — Two-Phase-Compaction, live-Scan, offset-Rewrite
  - H.8 (DDL-Lebenszyklus) ✅ — DROP/TRUNCATE/ALTER TABLE DROP COLUMN mit Sidecar-Cleanup
  - H.9 (Config, Telemetrie, Benchmarks, Doku) ✅ — BlobSidecarBenchmark, interne Counter, Options-PublicAPI
- **Nächster Schritt:** Phase E fortsetzen (E.4 Rollen + GRANTs)
