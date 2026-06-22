# Plan: WalhallaSql Embedded v1.0 Release

> Scope: Embedded-only NuGet-Release (WalhallaSql + AdoNet + EfCore).  
> C/S-Server (PgWire-Host, AuthN/AuthZ, Online-Backup) kommt danach.  
> DbUi wird als produktives internes Werkzeug stabilisiert.

---

## 1. Ziele und Akzeptanzkriterien

### Ziel
Ein produktionsreifer, embedded-first Release der NuGet-Pakete:
- `WalhallaSql`
- `WalhallaSql.AdoNet`
- `WalhallaSql.EfCore`

plus ein Dapper-Sample und ein produktiv nutzbares DbUi.

### Akzeptanzkriterien
1. **Core SQL Tests:** 100 % grün (aktuell 587/587).
2. **EF Core eigene Tests:** 100 % grün (aktuell ~313, 2 Fehler).
3. **EF Core P0/P1-Limits:** Alle aus `EF-CORE-LIMITS.md` behoben.
4. **ADO.NET Conformance:** Savepoints, `HasRows`, `RecordsAffected` bei IN-Clause stabil.
5. **Dapper:** Smoke-Tests grün für `Query<T>`, `QueryAsync<T>`, `Execute`, Parameter-Binding.
6. **DbUi:** Keine bekannten Crashes, Export CSV/JSON funktioniert, Streaming-Queries stabil.
7. **NuGet-Pakete:** Version `1.0.0`, XML-Doku, README in jedem Paket.
8. **Dokumentation:** Release-Notes, Migration-Guide from SQLite, Breaking-Changes-Doku.

---

## 2. Phase A — EF Core Hardening (P0/P1)

### A.1 DateOnly/TimeOnly in LINQ-Queries
**Problem:** `Where(e => e.BirthDate == new DateOnly(...))` liefert falsche Ergebnisse.  
**Ort:** `WalhallaSql.EfCore/WalhallaSqlSqlTranslatingExpressionVisitor.cs` und/oder Engine-Literal-Formatierung.  
**Lösung:** DateOnly/TimeOnly-Literale korrekt als SQL-Strings formatieren (`yyyy-MM-dd` / `HH:mm:ss.fffffff`) und Typ-Vergleich im Engine-Binder sicherstellen.

### A.2 `Contains` mit lokaler Collection
**Problem:** `Where(e => localArray.Contains(e.Id))` liefert nur erste Zeile.  
**Ort:** IN-Clause-Übersetzung oder Engine-Ausführung.  
**Lösung:** Mehrwertige IN-Listen vollständig expandieren und als `OR`-Kette / Engine-IN-Operator ausführen.

### A.3 `DbUpdateConcurrencyException` bei nicht existierenden Entities
**Problem:** `Remove(ghost)` + `SaveChanges()` wirft keine `DbUpdateConcurrencyException`.  
**Ort:** `WalhallaSql/Api/WalhallaEngine.cs` DELETE-PK-Fast-Path.  
**Lösung:** Im PK-Fast-Path prüfen, ob Zeile existierte; bei `Affected(0)` Exception werfen.

### A.4 Ambient EF Transactions auf `WalhallaSqlEfCoreContext`
**Problem:** `Database.BeginTransaction()` wirft `NotSupportedException`.  
**Ort:** `WalhallaSql.EfCore/WalhallaSqlEfCoreContext.cs` / `WalhallaSqlRelationalTransaction`.  
**Lösung:** Guardrail lockern und ADO.NET-Transaktion durchreichen.

### A.5 `ALTER COLUMN` Syntax
**Problem:** Migration Script Builder erzeugt `ALTER TABLE t ALTER COLUMN c TYPE` ohne `TYPE`.  
**Ort:** `WalhallaSql.EfCore/Migrations/WalhallaSqlMigrationScriptBuilder.cs`.  
**Lösung:** `TYPE`-Keyword hinzufügen; Engine-Handler anpassen.

### A.6 Unique Index Enforcement
**Problem:** `CREATE UNIQUE INDEX` akzeptiert Duplikate.  
**Ort:** `CheckUniqueConstraintBuffered` im MvccBPlusTree-Modus.  
**Lösung:** Unique-Constraint-Prüfung für MvccBPlusTree korrigieren.

### A.7 Migration Guardrails
**Problem:** Rename/Drop-Validierung fehlt.  
**Ort:** Engine-DDL-Validierung.  
**Lösung:** Column-Existenz und FK-Referenzen vor DDL prüfen.

---

## 3. Phase B — ADO.NET Conformance

### B.1 Savepoints
**Status:** Laut `EF-CORE-LIMITS.md` bereits behoben — muss verifiziert werden.  
**Aktion:** `AdoNetSurfaceConformanceTests` erneut laufen lassen.

### B.2 `HasRows` bei Streaming Reader
**Status:** Laut `EF-CORE-LIMITS.md` bereits behoben — muss verifiziert werden.

### B.3 `RecordsAffected` nach DELETE mit IN-Clause
**Problem:** `DELETE ... WHERE Id IN (1, 2)` liefert `1` statt `2`.  
**Ort:** Engine-IN-Clause-Ausführung im DELETE-Pfad.  
**Lösung:** Betroffene Zeilen pro IN-Element zählen.

### B.4 SQLSTATE/Fehlercode-Strategie
**Aktion:** Konsistente PostgreSQL-kompatible SQLSTATEs für häufige Fehler definieren und PublicAPI tracken.

---

## 4. Phase C — Dapper-Support

### C.1 Dapper-Smoke-Tests
Neues Testprojekt `WalhallaSql.Dapper.Tests` oder Erweiterung von `WalhallaSql.Tests`:
- `Query<T>` mit einfachem SELECT
- `QueryAsync<T>`
- `Execute` für INSERT/UPDATE/DELETE
- Parameter-Binding (anon. Objekte, DynamicParameters)
- `QueryMultiple` falls Zeit

### C.2 Dapper-Sample
Neues Projekt `WalhallaSql.Samples.Dapper`:
- Connection-String `:memory:`
- CRUD-Beispiel
- README-Verweis

### C.3 Provider-Factory-Registrierung sicherstellen
Prüfen, dass `DbProviderFactories.RegisterFactory("WalhallaSql", WalhallaSqlDbProviderFactory.Instance)` funktioniert.

---

## 5. Phase D — DbUi Produktivierung

### D.1 Stabilität
- Keine bekannten Crashes bei großen Streaming-Resultsets (heute behoben).
- Verbindungs-Management: Mehrere Tabs, reconnect, connection-loss-Handling.

### D.2 Feature-Lücken
- Query-History (letzte N Statements)
- Ergebnis-Grid: Column-Auto-Resize, Sortierung
- Export: CSV/JSON bereits vorhanden — auf größere Datenmengen testen
- Dark Mode / Theming (optional)

### D.3 Deployment
- DbUi als ClickOnce / single-file EXE?
- Eigene `.csproj` mit `<OutputType>WinExe</OutputType>` und Icon.

---

## 6. Phase E — Release Engineering

### E.1 Versionierung
- Alle drei Pakete auf `1.0.0` setzen.
- `Version`- und `PackageVersion`-Properties in `.csproj` synchronisieren.

### E.2 NuGet-Pakete
- `dotnet pack --configuration Release` muss alle drei Pakete fehlerfrei erzeugen.
- XML-Doku (`<GenerateDocumentationFile>true</GenerateDocumentationFile>`) aktivieren.
- README in jedes Paket einbinden (`<PackageReadmeFile>README.md</PackageReadmeFile>`).

### E.3 PublicAPI
- `PublicAPI.Shipped.txt` für `WalhallaSql` aktualisieren.
- RS0016-Warnungen in PgWire (neue Methoden) bereinigen.

### E.4 Dokumentation
- `WalhallaSql/README.md` aktualisieren.
- `docs/migration/from-sqlite.md` prüfen/ergänzen.
- `CHANGELOG.md` für v1.0.0 anlegen.
- `SECURITY.md` anlegen.

### E.5 CI/CD
- GitHub Actions Workflow für Build + Test + Pack.
- Smoke-Test nach Pack: NuGet-Pakete in leeres Projekt installieren und minimale Query ausführen.

---

## 7. Meilensteine und geschätzter Aufwand

| Phase | Meilenstein | Geschätzter Aufwand |
|---|---|---|
| A.1–A.4 | EF Core P0/P1 Bugs behoben, eigene Tests grün | 3–5 Tage |
| A.5–A.7 | Migration / Unique Index stabil | 2–3 Tage |
| B.1–B.4 | ADO.NET Conformance vollständig | 1–2 Tage |
| C.1–C.3 | Dapper-Support + Sample | 1–2 Tage |
| D.1–D.3 | DbUi produktiv nutzbar | 2–3 Tage |
| E.1–E.5 | Release Engineering | 2–3 Tage |
| **Gesamt** | **Embedded v1.0 Release** | **11–18 Tage** |

---

## 8. Risiken

1. **EF Core P1-Fixes könnten tiefer reichen** als geschätzt (z. B. DateOnly/TimeOnly im Engine-Binder).
2. **Unique Index Enforcement** könnte Performanz-Regressionen auf Schreibpfaden verursachen.
3. **DbUi WPF-Performance** bei sehr großen Resultsets (>500k Zeilen) bleibt ein Risiko.
4. **PublicAPI-Tracking** für `WalhallaSql.PgWire` ist aktuell unvollständig (RS0016-Warnungen).

---

## 9. Empfohlene Reihenfolge

1. Phase A.1–A.4 (EF Core P0/P1) — größter Blocker
2. Phase B (ADO.NET) — parallelisierbar mit A.5–A.7
3. Phase C (Dapper) — nach ADO.NET stabil
4. Phase D (DbUi) — kann parallel zu C laufen
5. Phase E (Release Engineering) — am Ende

---

## 10. Nicht im Scope von v1.0

- PgWire-Host Produktivierung (Phase E der Roadmap)
- Rollen + GRANTs
- Online-Backup
- Connection Pooling im Server
- Logical Replication
- Distributed/Sharded Setup
