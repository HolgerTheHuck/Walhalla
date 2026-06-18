# LayeredSql Projektplan (Orientierung & Delivery)

Stand: 21.02.2026

## Zielbild
Ein produktionsnahes System mit:
- klarer SQL-Konformitätsstrategie (Entry/Core-orientiert),
- stabilen EF- und ADO-Providern,
- optionalem .NET-only Storagebackend,
- Embedded- und Server-Modus,
- Admin-UI für Betrieb/Diagnose,
- Performance-Tuning inkl. In-Memory-Caching.

---

## Arbeitsstränge

### 1) SQL-Konformität (Entry/Core)
**Ziel:** Definierte SQL-Feature-Matrix mit Tests pro Feature.

Bereits vorhanden (Auszug):
- SELECT/WHERE, JOIN (INNER/LEFT), UNION
- DDL: CREATE/DROP TABLE, CREATE/DROP INDEX, ALTER TABLE (mehrere Varianten)
- DML: INSERT (VALUES), UPDATE, DELETE
- FK: single/composite, ON DELETE/ON UPDATE (RESTRICT/CASCADE)

Offen (priorisiert):
- `INSERT INTO ... SELECT ...` (Top-Priorität)
- `GROUP BY`, `HAVING`, Aggregatfunktionen
- Subqueries (mind. in WHERE)
- explizite SQL-Feature-Matrix (Soll/Ist/Testfall-Link)

Deliverables:
- `docs/SQL-Feature-Matrix.md`
- Tests je Feature (Parser + Executor)

---

### 2) EF-Core Provider
**Ziel:** Alltagstaugliche Migrationen + reproduzierbarer Betrieb.

Bereits stabil:
- Build + Sample laufen
- FK-Mapping inkl. Cascade/Restrict
- Migrations-History + Locking/Guardrails

Offen:
- Konformitätsmatrix EF-Operationen → SQL-Operationen
- harte Kanten dokumentieren (z. B. begrenzte SQL-Semantik)
- mehr E2E-Tests für Real-Modelle (Mehrtabellen, Rename/Alter-Ketten)

Deliverables:
- erweiterte Provider-Testmatrix
- aktualisierte Roadmap-Doku mit "supported/unsupported" je EF-Operation

---

### 3) ADO.NET Provider
**Ziel:** Einfache Nutzung aus beliebigen .NET-Apps/Tools.

Offen:
- API-Härtung (`DbConnection`, `DbCommand`, `DbDataReader`, Parameter)
- bessere Fehlerbilder + SQLSTATE-/Fehlercode-Strategie
- Kompatibilitätsprüfung mit Dapper/Standard-ADO-Pfaden

Deliverables:
- ADO-Kompatibilitätstests (Smoke + Integration)
- Doku "ADO usage patterns"

---

### 4) Storagebackend-Strategie (inkl. .NET-only Option)
**Ziel:** Klare Entscheidung für Default-Backend + optionales alternatives Backend.

Strang A (bestehendes Backend):
- WAL/Recovery weiter härten
- Langzeittests (Crash/Recovery/Konsistenz)

Strang B (.NET-only Backend Spike):
- Evaluationskriterien: Stabilität, Wartbarkeit, Durchsatz, Latenz, Lizenz, Betriebsmodell
- Kandidatensuche und Prototyping (2-3 Kandidaten)
- Entscheidung mit Messwerten statt Bauchgefühl

Deliverables:
- `docs/Storagebackend-Evaluation.md`
- ADR: Backend-Entscheidung

---

### 5) Betriebsmodi: Embedded + Server
**Ziel:** Gleiche SQL-Engine, unterschiedliche Host-Topologie.

Embedded:
- App-intern, minimaler Overhead

Server-Modus:
- SQL-/ADO-Transport primaer ueber PgWire; zusaetzliche Verwaltungs- oder Integrations-APIs koennen separat ueber REST laufen
- Multi-Client Concurrency, AuthN/AuthZ, Quotas
- Session-/Transaction-Semantik über Transportgrenzen

Deliverables:
- Protokoll-/API-Contracts
- Last-/Stabilitätstests im Serverbetrieb

---

### 6) Admin-UI
**Ziel:** Operative Bedienbarkeit ohne Debugging im Code.

MVP-Funktionen:
- DB/Collection/Schema-Browser
- SQL-Query-Konsole (read-only + optional write mit Schutz)
- Explain/Plan-Info, Indizes, FK-Übersicht
- Migration-/History-Ansicht
- Basis-Monitoring (QPS, Latenz, Fehler)

Deliverables:
- UI-MVP (read-only zuerst)
- Betriebsleitfaden

---

### 7) Performance & Caching
**Ziel:** Vorhersagbare Performance unter Last.

Schwerpunkte:
- Baseline-Benchmarks (read/write/mixed)
- Hot-Path-Profiling (Parser, Plan, Executor, IO)
- In-Memory-Caching (Plan-Cache, Metadaten-Cache, optional Result-Cache)
- Cache-Invalidation-Regeln + Metriken

Deliverables:
- `docs/Performance-Baseline.md`
- Tuning-Backlog mit messbaren Zielen (p50/p95/p99)

---

## Habe ich was vergessen? (wichtige Zusatzpunkte)
Ja, diese Punkte sollten explizit eingeplant werden:

1. **Security**
   - AuthN/AuthZ (v. a. Server-Modus)
   - Secrets-Handling
   - SQL-Sicherheitsregeln (Injection-Schutz, Rollenmodell)

2. **Observability**
   - strukturierte Logs, Tracing, Metriken
   - korrelierbare Request-/Query-IDs

3. **Backup/Restore & Disaster Recovery**
   - Backup-Format, Restore-Prozedur, Recovery-Ziele (RPO/RTO)

4. **Versionierung/Upgrade-Pfade**
   - On-Disk-Formatversionen
   - Upgrade-/Downgrade-Strategie

5. **Release Engineering**
   - Versioning, Packaging, CI/CD-Gates, Smoke-Checks

6. **Dokumentation als Produktbestandteil**
   - Supported/Unsupported-Matrix
   - Runbooks für Betrieb/Incident

---

## Priorisierte Roadmap (Vorschlag)

### Phase 0 (jetzt, 1-2 Wochen)
- SQL-Feature-Matrix anlegen
- `INSERT ... SELECT` umsetzen
- Baseline-Benchmarks + Metriken einführen

### Phase 1 (2-4 Wochen)
- EF/ADO Testmatrix erweitern
- Server-Modus Contract festziehen
- Admin-UI MVP (read-only)

### Phase 2 (4-8 Wochen)
- Aggregation/Subquery-Features
- Security + Observability auf Produktionsniveau
- Caching-Strategie produktiv schalten

### Phase 3 (parallel/entscheidungsgetrieben)
- .NET-only Storagebackend Spike + ADR
- ggf. Integration als optionales Backend

---

## Konkrete nächsten 5 Tasks
1. `INSERT INTO ... SELECT ...` (Parser + Statement + Executor + Tests)
2. SQL-Feature-Matrix mit Ampelstatus (grün/gelb/rot)
3. EF/ADO Kompatibilitätstests in CI erweitern
4. Performance-Baseline (3 Lastprofile) erzeugen
5. Admin-UI MVP Scope finalisieren (nur read-only)

---

## Entscheidungslog (offen)
- Welche zusaetzlichen Verwaltungs-/Integrations-APIs braucht der Server-Modus neben dem primaeren PgWire-SQL-Pfad?
- Welche Mindest-SQL-Features definieren wir verbindlich für "Entry/Core v1"?
- Welches Caching zuerst: Plan-Cache oder Result-Cache?
- Wird .NET-only Backend optional oder langfristiger Default?
