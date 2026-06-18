# Embedded-Ready Abnahmekatalog

Stand: 27.03.2026

Companion-Dokument: [Embedded-Ready Smoke-Checkliste](./Embedded-Ready-Smoke-Checklist.md)

## Zielbild
Dieses Dokument definiert verbindliche Abnahmekriterien für eine saubere Embedded-Version von LayeredSql ohne User-/Rechtemodell.

Definition „Embedded-Ready“:
- Einbettbar in Desktop-/Service-Anwendungen ohne externen DB-Server
- Stabiler lokaler Betrieb mit konsistentem Recovery-Verhalten
- Vorhersagbares Verhalten in SQL-, EF-, ADO.NET- und CLI-Pfaden

## Geltungsbereich
- In Scope: Engine, SQL-Ausführung, EF-Bridge/Provider-Subset, ADO.NET-Provider-Subset, CLI
- Out of Scope: AuthN/AuthZ, Multi-Tenant-Security, Netzwerk-/Server-Betrieb

## Release-Gates (global)

### MUST
- Solution-Build grün (`dotnet build LayeredSql.sln`)
- Keine testenden Regressionen in Kernprojekten (Engine + SQL + EF-Tests + CLI-Smokes)
- Datenkonsistenz nach Crash/Restart für bestätigte Operationen
- Dokumentierte Feature-Matrix mit klaren „supported / not supported“-Grenzen

### SHOULD
- Zielgerichtete Performance-Smoketests mit Baseline-Vergleich
- Reproduzierbare CLI-JSON-Ausgaben für GUI-Integration
- Definierte Fehlercodes und standardisierte Fehlermeldungen
- Deterministische Same-Path-Embedded-Sperren statt roher Datei-Freigabefehler

---

## 1) Engine (WalStoreEngine)

### MUST
- WAL/Recovery konsistent für Commit, Rollback, Delete
- Start nach ungeplantem Abbruch ohne Datenkorruption
- Nicht-unique Index-Semantik korrekt (inkl. Range-/Identity-Verhalten)
- Kein globaler, instanzübergreifender Runtime-State
- Alle Engine-Kerntests grün:
  - `WalStoreRuntimeTests`
  - `OdsSkeletonTests`
  - `OdsFuzzTests` (Standardlauf)

### SHOULD
- Long-Running-Fuzz-Suite über Env-Flag dokumentiert und manuell grün
- Rebalance-/Cache-Metriken sichtbar für Diagnose

### Exit-Kriterium
- 0 kritische Fehler in Engine-Testläufen, reproduzierbar über mindestens 2 lokale Runs

---

## 2) SQL-Ausführung (Mapper + Executor)

### MUST
- Unterstützter Scope stabil:
  - `SELECT` mit `WHERE`, `ORDER BY`, `TOP`, `LIMIT/OFFSET`
  - `GROUP BY`, `HAVING` (mit `GROUP BY`)
  - globale Aggregate (`COUNT`, `SUM`, `MIN`, `MAX`, `AVG`)
  - `CREATE/ALTER/DROP` im aktuell dokumentierten Umfang
  - `INSERT/UPDATE/DELETE`
  - `LEFT JOIN`, `UNION`/`UNION ALL`
- Statement-atomare Ausführung mit Engine-Transaktion
- Deterministische Fehler bei nicht unterstützten Features

### SHOULD
- Einheitliche SQL-Fehlercodes/-texte
- Erweiterte Matrix-Tests für Grenzfälle (NULL, leere Mengen, Typkonversionen)

### Exit-Kriterium
- `SqlStatementMapperTest` und `SqlStatementExecutorTest` vollständig grün

---

## 3) EF (Bridge/Provider-Subset)

### MUST
- Aktueller Scope explizit als „EF-Subset“ dokumentiert
- Include-/LINQ-Kernszenarien (gemäß Tests) stabil
- Migrations-Flow für unterstützte Operationen stabil
- Klare NotSupported-Fehler bei nicht implementierter EF-Semantik

### SHOULD
- Delta-Dokumentation pro EF-Feature (EF-Standard vs LayeredSql-Verhalten)
- Verbesserte Diagnose für unsupported Expression/Include-Fälle

### Exit-Kriterium
- `LayeredSql.EfCore.Tests` grün, insbesondere Include-/Migrations-Szenarien

Aktueller Nachweis:
- externer Paketkonsum ueber `Category=NuGetConsumerSmoke` verifiziert `LayeredSql.NuGetDemo` reproduzierbar gegen den lokalen Feed.

---

## 4) ADO.NET Provider

### MUST
- `DbConnection`, `DbCommand`, `DbTransaction`, `DbDataReader` für unterstützte SQL-Pfade stabil
- Parameterersetzung und Transaktionspfad konsistent
- Vorhersagbare Fehler bei nicht inferierbaren Statements

### SHOULD
- Erweiterte Kompatibilitätsmatrix (welche ADO.NET Patterns unterstützt sind)
- Verbesserte Statement-Inferenz bzw. explizite Option für Collection-Hinweise

### Exit-Kriterium
- ADO.NET Sample und externer ADO.NET-Paketconsumer laufen reproduzierbar und Kernpfade sind per Smoke-Test abgedeckt

Aktueller Nachweis:
- Embedded-Sample-Smoke ueber `Category=ADOEmbeddedSmoke` bestaetigt UPDATE-, Reader-, Scalar- und lokale Transaction-Kernpfade reproduzierbar.
- Externer Paketkonsum ueber `Category=ADONuGetConsumerSmoke` verifiziert `LayeredSql.AdoNet.NuGetDemo` reproduzierbar gegen den lokalen Feed.
- Same-Path-Embedded-Open ist zusaetzlich über gezielte Registry-/Lock-Regressionen fuer ADO und EF-Design-Time gehärtet.

---

## 5) CLI (Embedded Operations)

### MUST
- Kommandos stabil:
  - `status`
  - `sql`
  - `sql-file` (Multi-Statement)
  - `tx begin`
- Ausgabeoptionen stabil:
  - `--format text|json`
  - `--output <file>`
  - `--quiet` (nur mit `--output`)
- Exit-Codes dokumentiert und konsistent umgesetzt

### SHOULD
- `--output` als JSONL-Konvention für GUI-Consumer beschrieben
- Beispielskripte für GUI-Integration (Poll/Retry/Locking) vorhanden

### Exit-Kriterium
- CLI-Help, Status-, SQL- und SQL-File-Smokes grün; Exit-Code-Verhalten nachvollziehbar

---

## 6) Dokumentation / Betriebsreife

### MUST
- Zentrale Dokumente aktuell:
  - `docs/SQL-EF-Status.md`
  - `docs/Engine-Provider-Guide.md`
  - `LayeredSql.Cli/README.md`
- Jede bekannte Grenze ist als „nicht unterstützt“ dokumentiert

### SHOULD
- „GUI-Integration Contract“ dokumentiert (I/O-Format, Exit-Codes, Fehlerfälle)
- Changelog pro Meilenstein

### Exit-Kriterium
- Onboarding einer neuen Entwicklerin/eines neuen Entwicklers ohne Rückfragen zur Grundbedienung möglich

---

## Priorisierte nächste Umsetzung (Road to Embedded-Ready)

1. Test-Orchestrierung festziehen (eine konsolidierte Smoke-Pipeline für Engine/SQL/EF/ADO-Consumer/CLI)
2. SQL-Funktionsmatrix explizit vervollständigen (mit Positiv-/Negativfällen)
3. ADO.NET-Inferenzrobustheit verbessern (oder explizite Steuerung ergänzen)
4. EF-Subset schärfen: harte Grenzen + stabile Fehlermeldungen; Embedded-Lock und Migration-Lock sprachlich konsistent halten
5. GUI-Integration-Contract als separates Dokument ergänzen

## Abnahmeentscheidung
Die Embedded-Version gilt als „ready“, wenn alle MUST-Kriterien erfüllt sind und kein offener kritischer Defekt (P0/P1) in Engine/SQL/EF/ADO.NET/CLI vorliegt.
