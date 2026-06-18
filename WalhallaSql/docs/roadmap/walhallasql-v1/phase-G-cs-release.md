# Phase G — C/S v1.0 Release

**Ziel:** WalhallaSql als Postgres-kompatibler C/S-Server veröffentlichen — TPC-H-Benchmark erfüllt, Container-Distribution, 72h-Soak-Test grün, Production-Guide, Migrations-Guide Postgres→WalhallaSql.

**Voraussetzung:** Phasen E + F abgeschlossen.

**Exit-Kriterien**
- TPC-H Skala 1: ±2× Postgres auf ≥ 8 von 22 Hard-Goal-Queries
- Docker-Image auf GitHub Container Registry verfügbar
- 72h-Soak-Test ohne Memory-Leak, ohne Korruption, mit Random-Kill-Restart
- Tag `v1.0.0` mit signiertem Commit; Public-Announcement-Blogpost

---

## Slices

### G.1 — TPC-H-Benchmark (Skala 1)

**Scope**
- TPC-H-Datengenerator (`dbgen`) einbinden — Skala 1 = ~1 GB
- Schema-Loader via `walhallactl restore --schema tpch.sql`
- Bulk-Load via COPY (Phase E.3)
- Alle 22 TPC-H-Queries als ausführbare SQL-Datei
- BenchmarkDotNet-Runner gegen WalhallaSql + Postgres 16
- **Hard-Goal-Queries** (±2× Postgres):
  - Q1 (Pricing Summary Report)
  - Q3 (Shipping Priority)
  - Q5 (Local Supplier Volume)
  - Q6 (Forecasting Revenue Change)
  - Q10 (Returned Item Reporting)
  - Q12 (Shipping Modes and Order Priority)
  - Q14 (Promotion Effect)
  - Q19 (Discounted Revenue)
- **Tracking-Queries** (Performance-Messung, kein Hard-Goal): die restlichen 14
- Report-Skript: `scripts/run-tpch-comparison.ps1` → `docs/perf/tpch-comparison-v1.md`

**Verification** — Report committet, Hard-Goal erfüllt, Tracking-Queries dokumentiert (auch Regressions transparent)

**Files** — `WalhallaSql.Benchmarks/Tpch/*` (neu), `scripts/run-tpch-comparison.ps1`, `docs/perf/tpch-comparison-v1.md`

---

### G.2 — Container-Distribution

**Scope**
- `Dockerfile` (multi-stage)
  - Stage 1: `mcr.microsoft.com/dotnet/sdk:10.0` — Build + Publish
  - Stage 2: `mcr.microsoft.com/dotnet/runtime-deps:10.0` — minimal runtime
  - Endgröße-Ziel: < 100 MB
- `docker-compose.yml` Beispiel mit Health-Check + Volume-Mount
- Container-Entry-Point: `walhallactl start` mit Env-Var-Konfiguration (12-factor)
- GitHub Container Registry Push aus CI
  - Tags: `latest`, `v1.0.0`, `v1`, `v1.0`
  - Multi-Arch: `linux/amd64`, `linux/arm64`
- Helm-Chart in `deploy/helm/walhallasql/`
  - StatefulSet (Persistent-Volumes für Daten)
  - ConfigMap für Server-Config
  - Secret für SCRAM-Initial-Passwort
  - Service (ClusterIP + optional LoadBalancer)
  - HPA optional (nur read-replicas, sobald F.2 verfügbar)

**Verification** — `docker run -p 5432:5432 ghcr.io/walhallasql/walhallasql:v1.0.0` → `psql` verbindet; Helm-Install in lokalem `kind`-Cluster grün

**Files** — `Dockerfile`, `docker-compose.yml`, `deploy/helm/walhallasql/`, `.github/workflows/container-release.yml`

---

### G.3 — Production-Operations-Guide

**Scope** `docs/ops/`:

- `docs/ops/sizing.md` — RAM/Disk/CPU-Empfehlungen pro Workload-Klasse
- `docs/ops/tuning.md` — alle relevanten Konfigurationsparameter mit Erklärung
  - `walhalla.shared_buffers`
  - `walhalla.wal_sync_method` (`fsync` / `writethrough` / `none`)
  - `walhalla.max_connections`
  - `walhalla.work_mem` (für Hash-Joins, Sort)
  - `walhalla.maintenance_work_mem` (für VACUUM, ANALYZE, CREATE INDEX)
  - `walhalla.autovacuum_*`
- `docs/ops/backup-strategy.md` — Continuous-Archiving + PITR
- `docs/ops/monitoring.md` — Grafana-Dashboards (JSON in `deploy/grafana/`), Alerting-Regeln (PromQL)
- `docs/ops/troubleshooting.md` — häufige Probleme, Log-Patterns, Diagnose-Befehle
- `docs/ops/security-hardening.md` — Best Practices (TLS-only, Netzwerk-Isolation, GRANT-Audit, Rotation)

**Files** — alle obigen MD-Dateien, Grafana-JSON-Templates

---

### G.4 — Stress-/Soak-Tests

**Scope** — Erweiterung `WalhallaSql.PgWire.LoadTests` (bzw. neues `WalhallaSql.PgWire.LoadTests` nach Slice-6-Entscheidung).

- **72h Soak-Test**
  - Mixed-Workload: 60 % SELECT, 30 % INSERT/UPDATE, 10 % DELETE
  - 50 parallele Connections
  - alle 5 min `ANALYZE`, alle 30 min Vacuum
  - alle 60 min Backup im Hintergrund
  - alle 4h zufälligen `kill -9` → Auto-Restart → Konsistenz-Check
- **Memory-Leak-Detection**: dotnet-counters + dotnet-gcdump im 6h-Rhythmus, Diff
- **Korruptions-Check**: nach jeder Stress-Phase voller Storage-Walk + Checksum
- **Replication-Lag-Soak**: Publisher+Subscriber 72h, Lag bleibt < 1s p99

**Run-Umgebung**
- Dedicated CI-Runner (selbst-gehostet, da 72h)
- Ergebnis-Report als GitHub-Issue mit Charts

**Files** — `LayeredSql.PgWire.LoadTests/Soak/*` (neu), `.github/workflows/soak-test.yml` (cron-gesteuert, monatlich)

---

### G.5 — Migrations-Guide "Postgres → WalhallaSql"

**Scope** `docs/migration/from-postgres.md`:

- **pg_dump / pg_restore Kompatibilität**
  - SQL-Dump-Format (plain) — direkt einlesbar
  - Custom-Format (`-Fc`) — `walhallactl import pg-dump file.dump`
- **Inkompatibilitäten dokumentieren**
  - PL/pgSQL-Funktionen → manuell zu portieren (Postgres-Procedural-Language-Subset als v1.x-Backlog)
  - Erweiterungen wie PostGIS, pg_trgm → Mapping-Tabelle
  - Postgres-spezifische Operatoren (Geometric-Types) → Status pro Operator
  - Sequences (gut unterstützt) vs. Identity (Postgres-10+, primär unterstützt)
- **Schritt-für-Schritt-Migration** für eine reale Demo-DB (z. B. `dvdrental`-Sample)
- **Validierungs-Tool**: `walhallactl validate-pg-dump file.dump` zeigt nicht-unterstützte Features vor Import

**Files** — `docs/migration/from-postgres.md`, `LayeredSql.Migrator/Importers/PostgresImporter.cs`

---

### G.6 — Public-Beta-Programm → v1.0-GA

**Scope** — Geordneter Übergang von Beta zu GA.

**Vor v1.0-GA**
- Public-Beta-Tag `v1.0.0-beta.1` (Phase D-Abschluss + Phase E/F als Preview)
- Mindestens 4 Wochen Beta-Period mit ≥ 5 externen Testern (Discord, GitHub Discussions)
- Bug-Reports werden als Sub-Slices abgearbeitet
- Beta-Feedback-Issue-Template

**Bei v1.0-GA**
- Tag `v1.0.0` mit signiertem Commit
- GitHub-Release mit voller Changelog
- Blogpost (Markdown in `docs/blog/v1-launch.md` als Quelle)
- Tweet/Mastodon/HN-Announcement
- NuGet.org Push aller Pakete
- Container-Push zu ghcr.io
- Updates auf Top-Level-`README.md` und Webseite

**Nach v1.0**
- v1.x-Backlog publik (CYCLE-Detection, PITR, Physical-Replication, GIST, Row-Level-Security, Column-GRANTs)
- Roadmap v2 öffnen

**Files** — `docs/blog/v1-launch.md`, `.github/release/v1.0.0.md` (Release-Notes-Template)

---

## Verification (phasenübergreifend)

- **TPC-H-Report** in `docs/perf/` committet, Hard-Goal erfüllt
- **Container-Run** auf frischer Maschine: `docker run` → `psql` → 22 TPC-H-Queries laufen
- **72h-Soak** grün protokolliert
- **Helm-Install** auf `kind` grün
- **Migrations-Demo**: `dvdrental`-Sample erfolgreich migriert + 10 typische Queries vergleichbar

## Reihenfolge & Parallelisierbarkeit

```
G.1 (TPC-H)        ─── kann parallel zu G.2 starten
G.2 (Container)    ─── parallel zu G.1
G.3 (Ops-Guide)    ─── parallel, baut auf G.1 + G.2 Ergebnissen auf
G.4 (Soak)         ─── nach G.1 + G.2 (braucht stabile Binaries)
G.5 (Migration)    ─── parallel
G.6 (Beta → GA)    ─── am Ende; alles davor muss grün sein
```

## Geschätzte Slice-Anzahl

6 Slices (G.1–G.6).
