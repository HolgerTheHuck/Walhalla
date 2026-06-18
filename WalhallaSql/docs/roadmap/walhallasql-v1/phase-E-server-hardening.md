# Phase E — C/S Server Hardening

**Ziel:** PG-Wire-Server produktionstauglich machen — SCRAM-SHA-256 Auth, TLS, COPY-Protocol, Rollen + GRANTs, internes Connection-Pooling, Online-Backup.

**Voraussetzung:** Phasen A + B + C + D abgeschlossen. Embedded v1.0 released → C/S baut darauf auf.

**Exit-Kriterien**
- `psql` CLI verbindet mit SCRAM-Auth + TLS
- `psql \copy` (Client-Side) und `COPY FROM/TO STDIN/STDOUT` (Server-Side) funktionieren
- Rollen + GRANTs erzwungen auf SELECT/INSERT/UPDATE/DELETE/REFERENCES/TRIGGER
- Online-Backup während Last produziert konsistenten Restore (Property-Test)

---

## Slices

### E.1 — SCRAM-SHA-256 (RFC 7677) ✅

**Scope**
- User-Store: System-Tabelle `walhalla_authid` (Postgres-`pg_authid`-Äquivalent) mit Spalten `rolname`, `rolpassword` (SCRAM-Format `SCRAM-SHA-256$<iter>:<salt>$<storedkey>:<serverkey>`)
- `CREATE ROLE name WITH LOGIN PASSWORD 'plain'` → SCRAM-Hashing
- Auth-Flow in PG-Wire-Backend
  - SASL-Selection (`AuthenticationSASL` mit `SCRAM-SHA-256`)
  - SASL-Continue / SASL-Final
  - Channel-Binding optional (`SCRAM-SHA-256-PLUS` mit TLS-Endpoint)
- Iteration-Count konfigurierbar (default 4096, Postgres-konform)

**Test-Szenarien**
- `psql` (echter Client) verbindet erfolgreich
- Npgsql verbindet mit SCRAM
- Falsches Passwort → `SQLSTATE 28P01`
- Channel-Binding mit TLS — testbar mit `psql` neuer Version

**Files** — `WalhallaSql.PgWire/Auth/ScramSha256.cs` (neu), Erweiterung `WalhallaSqlPgWireBackend`, `WalhallaSql/Catalog/AuthIdTable.cs`

---

### E.2 — TLS-Server-Verbindungen ✅

**Scope**
- `SSLRequest`-Handshake (Postgres-Protocol): Client sendet Magic-Bytes, Server antwortet `'S'` (TLS) oder `'N'` (Plaintext)
- Cert-Loading
  - PEM-Files (`walhalla.crt`, `walhalla.key`) — Postgres-konform
  - X.509-Store auf Windows (`certstore://LocalMachine/My/<thumbprint>`)
  - Optional: Let's-Encrypt-Hook (ACME-Client als v1.x)
- Cipher-Suites: TLS 1.2 minimum, TLS 1.3 preferred; FIPS-konforme Suites optional
- Client-Cert-Auth (`pg_hba.conf`-Äquivalent: `walhalla_hba`) als v1.x-Backlog
- WebSocket-Tunnel: TLS auf der WebSocket-Schicht statt PG-Wire-Schicht — separat dokumentieren

**Files** — `WalhallaSql.PgWire/Tls/*` (neu), Config-Erweiterung

---

### E.3 — COPY-Protocol (Bulk-Loading) ✅

**Scope**
- `COPY tbl FROM STDIN [WITH (FORMAT TEXT|CSV|BINARY)]`
- `COPY tbl TO STDOUT [WITH (FORMAT TEXT|CSV|BINARY)]`
- `COPY tbl FROM STDIN WITH (FORMAT BINARY)` — Postgres-Binary-Format mit Header + Trailer
- Optionen: `DELIMITER`, `NULL`, `HEADER`, `QUOTE`, `ESCAPE`, `ENCODING`
- Server-Side: `COPY tbl FROM '/path/to/file'` (Superuser-only)
- Hook in `WalhallaSqlPgWireBackend` für `CopyInResponse` / `CopyData` / `CopyDone` / `CopyOutResponse`
- Performance-Ziel: ≥ 100k Rows/s auf Standard-Workload (10 int/text-Spalten)

**Test-Szenarien**
- `psql -c "\\copy tbl from 'data.csv' (format csv, header)"` lädt korrekt
- `pg_dump`-Output (CSV) lädt zurück
- Binary-Format-Round-Trip
- Fehler-Behandlung: bad row → Tx-Abort, Error-Message mit Zeilennummer

**Files** — `WalhallaSql.PgWire/Copy/*` (neu), Executor-Hook für COPY-Statements

---

### E.4 — Rollen + GRANTs

**Scope**
- `CREATE ROLE / DROP ROLE / ALTER ROLE`
- `GRANT [SELECT|INSERT|UPDATE|DELETE|REFERENCES|TRIGGER|TRUNCATE] ON tbl TO role`
- `GRANT USAGE ON SCHEMA s TO role`
- `GRANT EXECUTE ON FUNCTION f TO role`
- `REVOKE ...`
- System-View `walhalla_role_table_privileges` (Postgres-View-Äquivalent)
- Privilege-Check im Executor vor jeder Operation
- Default-Privileges: Owner hat alle Rechte, public hat nichts (Postgres-default seit 15)
- Role-Membership: `GRANT role1 TO role2` (Inheritance), `SET ROLE`

**Backlog für v1.x**
- Row-Level-Security (`CREATE POLICY`)
- `WITH GRANT OPTION` cascade
- Column-level GRANTs

**Files** — `WalhallaSql/Catalog/RoleManager.cs` (neu), `WalhallaSql/Authorization/PrivilegeCheck.cs` (neu)

---

### E.5 — Connection Pooling intern

**Scope** — Aktuell ggf. task-per-connection. Ziel: Worker-Pool.

- Konfigurierbare Limits: `max_connections` (default 100), `worker_threads` (default = CPU-Count)
- Connection-Lifecycle: Accept → Handshake → Auth → Worker-Pool-Übergabe → Message-Loop → Cleanup
- Backpressure: bei Pool voll → `AuthenticationFailed` mit "too many connections" oder Queue mit Timeout
- Pro Connection: `ConnectionId`, `BackendKeyData` (für `CancelRequest`-Protocol)
- Telemetrie: `pgwire.active-connections`, `pgwire.pool-utilization`, `pgwire.connection-wait-ms`

**Files** — `WalhallaSql.PgWire.Host/ConnectionPool.cs` (neu), bestehende Backend-Loop refactoren

---

### E.6 — Online-Backup (pg_basebackup-Style)

**Scope**
- Konsistenter Storage-Snapshot ohne Stop-the-World
- Mechanismus: WAL-Position `pg_start_backup()` → tar-Stream der Daten-Verzeichnisse → `pg_stop_backup()` → WAL-Segmente bis Backup-End-Position
- Tool: `walhallactl backup [--output path] [--compress gzip|zstd]`
- WAL-Archivierung-Hook
  - Shell-Command (`archive_command = 'cp %p /archive/%f'`)
  - Direct-S3 (AWS-SDK) und Azure-Blob (Azure-SDK) als optionale Plug-ins
- Restore-Tool: `walhallactl restore [--from path] [--target-time '2026-06-01 12:00:00']`
- Point-in-Time-Recovery (PITR) als Folge-Slice in v1.x

**Test-Szenarien (Property-basiert)**
- Backup während Schreib-Workload → Restore → Daten-Konsistenz-Check
- Backup-Validation: Hash-Check pro Datei
- Restore zu falschem Zeitpunkt → klarer Fehler

**Files** — `WalhallaSql.Cli/Commands/Backup.cs`, `WalhallaSql/Backup/*` (neu), CLI-Erweiterungen

---

### E.7 — WebSocket-Tunnel als offizielle Variante

**Scope** — WebSocket-Tunnel existiert bereits (`WalhallaSqlPgWireWebSocketTunnel`). Aufgabe: zur ersten-Klasse-Citizen machen.

- Doku: `docs/ops/websocket-tunnel.md`
- Sample: Browser-Client mit JavaScript, der via WebSocket SQL-Queries gegen WalhallaSql ausführt
- Authentifizierung über WebSocket-Layer (Bearer-Token, OAuth)
- TLS-Termination auf WebSocket-Layer
- Performance-Vergleich vs. TCP/Unix-Socket dokumentiert

**Files** — `docs/ops/websocket-tunnel.md`, `samples/web-sql-playground/` (optional)

---

## Verification (phasenübergreifend)

- **psql-Kompatibilitäts-Matrix**: Connect, Auth, einfache Queries, Prepared, COPY — alles funktioniert
- **Npgsql-Smoke**: aktueller Npgsql (8.x) verbindet, EF-Core via Npgsql-Provider funktioniert (eingeschränkt; voller Support kommt mit eigenem Provider in Phase D)
- **TLS-Tests** mit `openssl s_client -connect host:port -starttls postgres`
- **Backup-Soak**: 4h-Workload, Backup, Restore auf neuer Instanz, Konsistenz-Check

## Reihenfolge & Parallelisierbarkeit

```
E.1 (SCRAM) ─── BLOCKER (psql verbindet nicht ohne Auth)
E.2 (TLS)   ─── parallel zu E.1
E.3 (COPY)  ─── nach E.1
E.4 (Rollen) ── nach E.1
E.5 (Pool)  ─── nach E.1
E.6 (Backup) ── parallel ab Phase-Start (unabhängig von Wire-Slices)
E.7 (WS)    ─── parallel, primär Doku
```

## Geschätzte Slice-Anzahl

7 Slices (E.1–E.7).
