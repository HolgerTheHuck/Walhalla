# EFCore `dotnet ef` Roadmap (LayeredSql)

Stand: 21.02.2026

## Status
- Phase 1: abgeschlossen.
- Phase 2: MVP abgeschlossen (mit bewusstem Scope-Limit, siehe offene Punkte).
- Phase 3: gestartet (Kickoff unten).

## Zielbild
Eine vollständige `dotnet ef`-Migrationspipeline bedeutet:
- `dotnet ef migrations add` erzeugt stabile Migrationen.
- `dotnet ef database update` kann gegen euren Provider migrieren.
- Model Snapshot, History, SQL-Generierung und Design-Time-Discovery funktionieren ohne Sonderwege.

---

## Phase 1 — Tooling-fähiger Provider-Kern (MVP)
Ziel: `dotnet ef` soll den Context + Provider sauber laden und einfache Migrationen durchlaufen.

### Deliverables
1. **Design-Time Bootstrapping**
   - `IDesignTimeDbContextFactory<TContext>` im EF-Projekt oder Sample.
   - Konsistente `UseLayeredSql(...)`-Erweiterung inkl. Provider-Service-Registrierung.

2. **Provider Service-Registrierung (minimal)**
   - Basis-Implementierungen/Registrierungen für:
     - `IMigrationsAssembly`
     - `IHistoryRepository`
     - `IMigrator`
     - `IMigrationsSqlGenerator` (oder äquivalente Statement-Pipeline)
   - Deterministisches Laden der Metadaten (`__common_schema` first, `__sql_catalog` overlay).

3. **History/Locking-Basis**
   - Migration-History als verbindliche Quelle (nicht nur optionales Log).
   - Einfacher Prozess-Lock für `Migrate()` (mindestens single-writer im selben DB-Pfad).

### Akzeptanzkriterien
- `dotnet ef migrations add Initial` läuft ohne manuelle Workarounds.
- `dotnet ef database update` erstellt Tabellen und schreibt History.
- Zweiter Lauf ist idempotent (keine Doppelanwendung).

---

## Phase 2 — Vollständiger Migrations-Diff + SQL/DDL-Abdeckung
Ziel: Der Generator deckt die üblichen EF-Migrationsoperationen robust ab.

Aktueller Zwischenstand (21.02.2026):
- Rename-Heuristiken sind gehärtet: mehrdeutige Table/Column-Renames führen jetzt zu klaren Fehlern statt stiller Fehlzuordnung.
- Explizite NotSupported-Pfade sind aktiv für aktuell nicht abgedeckte Modellfeatures:
   - Foreign Keys
   - Alternate Keys
   - Multi-Column Indexes
- Index-Diff ist aktiv (single-column): Create/Drop sowie Recreate bei geänderten Eigenschaften werden geplant und angewendet.
- ALTER-Guardrails aktiv:
   - Blockiert automatische NULLABLE -> NOT NULL Verengung ohne expliziten Backfill-Schritt.
   - Blockiert potenziell verlustbehaftete/ambige Typwechsel (z. B. `string -> numeric`, `binary`-Konvertierungen, `double -> int`).

Formaler Abschluss für Phase 2 (MVP):
- ✅ Diff-/Apply-Pfad für den aktuell unterstützten Scope ist stabil.
- ✅ Ambiguitäten und risikoreiche Alters werden explizit geblockt statt still fehlinterpretiert.
- ⚠️ Offener Restscope: Alternate Keys, Multi-Column Indexes.

FK-Update (21.02.2026):
- ✅ SQL-MVP für Foreign Keys umgesetzt (`ADD/DROP CONSTRAINT`, RESTRICT-Prüfungen bei INSERT/UPDATE/DELETE).
- ✅ EF-Migrations-Mapping für FK Add/Drop umgesetzt (single + composite FK).
- ⚠️ Weiterhin nicht abgedeckt: Cascade/SetNull-Verhalten.

### Deliverables
1. **Model-Diff-Komplettierung**
   - Zuverlässige Erkennung für:
     - Create/Drop/Rename Table
     - Add/Drop/Rename/Alter Column
     - Create/Drop Index
   - Rename-Entscheidungen konfigurierbar (Heuristik + opt-in Hinweise).

2. **Operation-Mapping & DDL-Härtung**
   - Abbildung von EF-Migrationsoperationen auf eure SQL-Statements.
   - Saubere Fehlerbilder bei nicht unterstützten Features (statt stiller Teilmigration).

3. **Type Mapping + Annotation Roundtrip**
   - Konsistente CLR↔SQL↔CoreDataType Abbildung.
   - Annotationen (Nullable, PK, Unique, ggf. Default) stabil im Snapshot verwendbar.

### Akzeptanzkriterien
- `add/update/remove/script` funktionieren für den abgedeckten Feature-Scope.
- Snapshot-Diff ist deterministisch über mehrere Läufe.
- Kein stilles Metadata-Drift-Verhalten (bereits mit Drift-Check begonnen).

---

## Phase 3 — Produktionsreife (`dotnet ef` parity-orientiert)
Ziel: Stabile Pipeline unter Last, Teambetrieb und CI/CD.

Kickoff (nächste konkrete Schritte):
1. **Concurrency-Tests**
   - ✅ Lock-Contention-Smoketest im EF-Sample umgesetzt (Timeout-basierter Single-Writer-Nachweis).
   - ✅ E2E-Skript führt jetzt einen echten Multi-Process-Lauf mit zwei parallelen `dotnet ef database update` Aufrufen gegen denselben DB-Pfad aus.
   - Aktuell verifizierte Semantik: der zweite Runner scheitert deterministisch bereits im Design-Time-Open-Pfad mit `Could not acquire embedded database lock ...`, bevor der Embedded-Storage auf `ods.dat` zugreift.
2. **Historien-Konsistenzprüfungen**
   - ✅ Guard für „applied migration nicht im Assembly-Satz“ ist aktiv.
   - ✅ Klarer Fehlertext mit Handlungsanweisung (repair/explicit target).
   - Hinweis: Legacy-`Auto_...` Fallback-IDs werden für Rückwärtskompatibilität toleriert.
3. **CI-E2E-Pipeline**
   - ✅ Reproduzierbares Skript angelegt: `scripts/ci-ef-e2e.ps1`.
   - ✅ GitHub-Actions-Workflow angelegt: `.github/workflows/ef-e2e-gate.yml`.
   - ✅ Vorgesehener Required-Check-Name: `EF E2E Gate / ef-e2e`.
   - Pipeline-Schritte: `migrations add` (smoke, tolerant bei "No changes"), `database update`, Multi-Process-Same-Path-Contention-Probe, Sample-Run.

### Deliverables
1. **Transaktions- und Concurrency-Semantik**
   - Migrationsausführung mit klarer Isolation.
   - Verlässliche Sperrstrategie für parallele Runner/Instanzen.

2. **Feature-Tiefe**
   - Erweiterte Operationen (Defaults, Constraints/FKs je nach Zielumfang).
   - Präziser Umgang mit riskanten Alter-Operationen (Datenkonvertierung, Backfill, Rollback-Hinweise).

3. **Tooling & Testmatrix**
   - End-to-End Tests mit `dotnet ef` CLI in CI.
   - Kompatibilitätsmatrix für unterstützte EF-Core-Versionen.
   - Golden-file Tests für Migration-Script-Ausgabe.

### Akzeptanzkriterien
- Mehrere realistische Modelländerungszyklen in CI ohne manuelle Eingriffe.
- Vorhersagbare Fehlermeldungen bei nicht unterstützten Operationen.
- Dokumentierte Upgrade-/Betriebsstrategie.

---

## Konkrete Gap-Liste für euer aktuelles Repo
Bereits vorhanden:
- Eigenes Migration-Planning/Apply + History.
- DDL/DML-Grundlage inkl. `ALTER`/`RENAME`-Pfad.
- Common-Schema-Strategie mit Drift-Prüfung.

Fehlt für **vollständiges** `dotnet ef`:
1. Vollständige EF-Migrationsservice-Pipeline statt hybrider Bridge.
2. Breitere Operation-Abdeckung (v. a. FK/Constraints/Multi-Column-Index).
3. CI-validierte End-to-End `dotnet ef` Testkette.

---

## Empfohlene Reihenfolge (kurz)
1. Phase 1 komplettieren (CLI lädt Provider stabil).
2. Danach Diff/Operationen (Phase 2), um funktionalen Scope abzusichern.
3. Zum Schluss Concurrency/CI/Parität (Phase 3).

Diese Reihenfolge minimiert Rework und macht früh sichtbare Fortschritte für ein echtes Testprojekt mit `dotnet ef`.
