# Phase F — Replikation & Ops

**Ziel:** Logische Replikation (Subscriber + Publisher), OpenTelemetry-Integration (Traces + Metrics + Logs), Query-Statistik-View, Admin-CLI, Health-Endpoints.

**Voraussetzung:** Phase E abgeschlossen.

**Exit-Kriterien**
- Logical Replication zwischen zwei WalhallaSql-Instanzen funktioniert deterministisch
- WalhallaSql kann von Postgres-Publisher Daten konsumieren (Subscriber-Side)
- OpenTelemetry-Traces in lokalem Jaeger sichtbar; Metrics in Prometheus scrapebar
- `walhallactl` deckt init/start/stop/status/backup/restore/user/analyze ab
- Health-/Readiness-Endpoints für K8s vorhanden

---

## Slices

### F.1 — Logical Replication (Publisher + Subscriber)

**Scope**
- **Publisher-Side**
  - WAL-Decoder, der logische Change-Events erzeugt (INSERT/UPDATE/DELETE pro Row)
  - Postgres-Logical-Replication-Protocol-Subset implementieren
  - `CREATE PUBLICATION pub FOR TABLE t1, t2 [, ...]`
  - `CREATE PUBLICATION pub FOR ALL TABLES`
  - Replication-Slot-Management (persistent + temporary)
  - WAL-Retention für aktive Slots
- **Subscriber-Side**
  - `CREATE SUBSCRIPTION sub CONNECTION '...' PUBLICATION pub`
  - Replication-Worker als Background-Task
  - Apply-Worker schreibt empfangene Changes in lokale Tabellen
  - Conflict-Detection (basic): bei Konflikt → Error + Slot pausiert
- **Cross-Compat-Ziel**
  - WalhallaSql als Subscriber gegen Postgres-15+-Publisher (read-only-Konsumption)
  - WalhallaSql ↔ WalhallaSql voll bidirektional möglich

**Test-Szenarien**
- 100k INSERT auf Publisher → exakt 100k auf Subscriber, gleicher Inhalt
- UPDATE/DELETE replizieren mit korrekter Reihenfolge
- DDL-Replication als v1.x (Postgres unterstützt es erst ab 16)
- Network-Drop + Resume: kein Datenverlust, exactly-once dank Slot-Position

**Files** — `WalhallaSql.PgWire/Replication/*` (neu), `WalhallaSql/Replication/*` (neu, Subscriber-Worker)

---

### F.2 — Streaming Replication (Physical) *[v1.x — Backlog]*

**Scope** — Physische Replikation auf WAL-Byte-Ebene, primär für High-Availability + Read-Replicas.

**Entscheidung**: in v1 zurückstellen. Logische Replikation deckt 80 % der Use-Cases. Physical Replication ist wichtiger für HA-Setups, aber komplexer (synchroner Commit, Failover-Logik).

**Status**: dokumentiert als Roadmap, nicht aktiv geplant für v1.

---

### F.3 — OpenTelemetry-Integration

**Scope**
- **Traces**
  - Span-Hierarchie pro Query: Connection → Parse → Plan → Execute → Fetch → Project → Send
  - Span-Attribute: `walhalla.sql_text` (truncated), `walhalla.rows_affected`, `walhalla.plan_strategy`
  - `Activity.Source = "WalhallaSql"` für ActivityListener-Integration
- **Metrics** (via `System.Diagnostics.Metrics`)
  - `walhalla.connections.active` (UpDownCounter)
  - `walhalla.queries.duration_ms` (Histogram, mit Tag `query_type`)
  - `walhalla.transactions.committed_total` / `aborted_total`
  - `walhalla.cache.hit_ratio` pro Cache (Plan, Where, OdsPager)
  - `walhalla.locks.contention_wait_ms` (Histogram)
  - `walhalla.wal.write_bytes_total`, `walhalla.wal.fsync_duration_ms`
- **Logs** — strukturiert via `ILogger<T>` mit Scopes:
  - `OperationId` für Cross-Layer-Korrelation
  - `ConnectionId` für PG-Wire-Korrelation
  - Log-Levels nach .NET-Convention; keine PII (SQL-Texte sind opt-in)
- **Exporter** — OTLP-Export (gRPC + HTTP/protobuf), Console-Exporter für Dev

**Test-Szenarien**
- lokaler Jaeger empfängt Traces einer SELECT-Query mit kompletter Span-Hierarchie
- Prometheus scrapet Metrics-Endpoint korrekt
- Korrelation: ein PG-Wire-Query erzeugt korrelierte Logs + Traces + Metrics

**Files** — `WalhallaSql/Diagnostics/Otel*.cs` (neu, baut auf existierendem `WalhallaSql.Diagnostics.*` auf), `WalhallaSql.PgWire.Host/MetricsEndpoint.cs`

---

### F.4 — pg_stat_statements-Äquivalent

**Scope**
- System-View `walhalla_stat_statements` mit Spalten:
  - `query_hash` (stabile Normalisierung: Literale zu `?`)
  - `query_text` (normalisierte Form)
  - `calls`, `total_exec_time_ms`, `mean_exec_time_ms`, `min/max/stddev`
  - `rows` (Summe), `shared_blks_hit/read` als WalhallaSql-Äquivalente
  - `last_executed_at`
- Konfigurierbar: `walhalla.track_stmt_stats = on|off`, `walhalla.stmt_stats_max = 10000` (LRU bei Überlauf)
- Verfügbar via `SELECT * FROM walhalla_stat_statements ORDER BY total_exec_time_ms DESC LIMIT 20`
- `walhalla_stat_statements_reset()` Funktion zum Zurücksetzen

**Files** — `WalhallaSql/Diagnostics/StmtStats.cs` (neu), Catalog-Erweiterung für virtuelle System-View

---

### F.5 — Admin-CLI (`walhallactl`)

**Scope** — vorhandenes `WalhallaSql.Cli` rebranden + erweitern.

**Subcommands**
- `walhallactl init [--data-dir path] [--config config.yaml]` — Daten-Dir initialisieren
- `walhallactl start [--data-dir path] [--port 5432] [--listen 0.0.0.0]`
- `walhallactl stop [--data-dir path]` (graceful shutdown via Unix-Socket / Named-Pipe)
- `walhallactl status [--data-dir path]` — JSON-Status (active connections, WAL-pos, replication-slots)
- `walhallactl backup` (E.6)
- `walhallactl restore` (E.6)
- `walhallactl user create <name> [--password '...'] [--role 'admin|readonly']`
- `walhallactl user list`
- `walhallactl analyze [table_name|--all]`
- `walhallactl vacuum [table_name|--all] [--full]`
- `walhallactl explain "<sql>"` — Plan-Ausgabe ohne Execution

**Anforderungen**
- Konsistente Output-Formate: `--format text|json|yaml`
- Cancellation via Ctrl-C über `CancellationToken`
- Exit-Codes nach Convention (`0` ok, `1` user-error, `2` system-error, `130` cancel)

**Files** — `LayeredSql.Cli/Commands/*` (Erweiterung), Rebrand zu `WalhallaSql.Cli` als Folge-Slice

---

### F.6 — Health-/Readiness-Endpoints

**Scope** — HTTP-Seitenkanal für K8s-Probes.

- Endpoint `:9090/health/live` — immer 200, solange Prozess läuft
- Endpoint `:9090/health/ready` — 200 wenn:
  - WAL recovered
  - Catalog geladen
  - Mindestens 1 free worker im Pool
  - Storage-Disk-Latency unter Threshold (configurable)
- Endpoint `:9090/metrics` — Prometheus-Exposition-Format (komplementär zu F.3 OTLP)
- Konfigurierbar via `walhalla.health_listen = '0.0.0.0:9090'`
- TLS optional auf demselben Listener

**Files** — `WalhallaSql.PgWire.Host/HealthEndpoint.cs` (neu)

---

## Verification (phasenübergreifend)

- **Replikations-Soak**: 1h-Lauf, Publisher mit 1k TPS, Subscriber holt auf ohne Lag-Akkumulation, Daten-Hashes identisch
- **OTel-Round-Trip**: lokales Jaeger + Prometheus + Loki, ein Query erzeugt Traces, Metrics, Logs alle korreliert via `OperationId`
- **CLI-Smoke**: Skript fährt komplettes Lifecycle (`init → start → user create → backup → stop → restore → start → query`)
- **K8s-Smoke**: Helm-Chart deployt WalhallaSql, Liveness + Readiness werden grün, Metrics scrapebar

## Reihenfolge & Parallelisierbarkeit

```
F.1 (Logical-Repl) ─── größter Slice
F.3 (OTel)         ─── parallel zu F.1
F.4 (StatStats)    ─── parallel
F.5 (CLI)          ─── nach E.6 (Backup-Commands kommen aus Phase E)
F.6 (Health)       ─── parallel
F.2 (Phys-Repl)    ─── v1.x, nicht in v1
```

## Geschätzte Slice-Anzahl

5 Slices in v1 (F.1, F.3, F.4, F.5, F.6) + F.2 als v1.x-Backlog.
