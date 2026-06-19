# Plan: Query-Optimierung im WalhallaSql ADO.NET-Provider

## Ziel

Wiederholte Abfragen über den ADO.NET-Provider sollen den vorhandenen Engine-Plan-Cache nutzen und nicht bei jedem `ExecuteReader()`/ExecuteNonQuery()` neu geparst/geplant werden. Dazu implementieren wir:

1. **Compiled-Statement-Cache auf ADO.NET-Ebene** – `WalhallaSqlDbCommand` hält ein gecachtes `WalhallaPreparedStatement` pro normalisierter SQL-Text.
2. **Parameter-Bindung statt Literal-Rewrite** – Parametrisierte SELECTs werden über `WalhallaEngine.Prepare()` mit Platzhaltern (`@p0`, `@p1`, …) ausgeführt, statt dass `SqlLiteralFormatter` die Werte in den SQL-String einbettet.
3. **Best-Effort Connection-Session-Pooling** (InProcess) – `WalhallaSqlDbConnection.Close()` gibt die `ISqlClientSession` zurück in einen kleinen Pool, statt sie jedes Mal neu zu erstellen.

## Stand (2026-06-19)

- Punkt 3 (Join-Allokationen) verifiziert: Benchmarks erfüllen ≥30 % weniger Allocations und ≥20 % Zeitersparnis.
- Punkt 1 (ADO.NET Prepared-Statement-Cache + Session-Pool) implementiert; `AdoNetPreparedStatementTests` (5 Tests) bestanden.
- Punkt 2 (PgWire Prepared-Statement/Portal-Cache) implementiert; `WalhallaSql.PgWire.Tests` (20 Tests) bestanden.
- `WalhallaSql.Tests` (504 Tests) weiterhin grün.
- `WalhallaSql.EfCore.Tests` hat 8 bekannte Beta-Fehler, die unabhängig von diesen Änderungen bereits auf dem Clean-Branch reproduzierbar sind.

## Aktueller Zustand

- `WalhallaEngine` besitzt bereits `_planCache` (`BoundedLruCache<CompiledPlan>`), der von `Prepare()` und `ExecuteSelect()` genutzt wird.
- Der ADO.NET-Provider ruft aber in `WalhallaSqlClientSession.Execute`/`ExecuteStream` immer `SqlLiteralFormatter.RewriteParametersAsLiterals(command)` auf. Dadurch entsteht für jeden Parameterwert ein neuer SQL-String, der Plan-Cache-Schlüssel ändert sich und der Engine-Cache wird wirkungslos.
- `WalhallaSqlDbCommand` baut bereits ein `PreparedCommandTemplate` (Auto-Prepare), das die Parameter normalisiert (`p_{name}` / `p{index}`), aber es wird nur verwendet, um einen strukturierten `SqlClientCommand` zu bauen – dieser landet dann trotzdem wieder im Literal-Formatter.
- `WalhallaSqlDbConnection.Close()` disposed die `ISqlClientSession` und gibt den Engine-Lease zurück; eine neu geöffnete Verbindung erzeugt eine neue Session.
- `WalhallaPreparedStatement.Execute()` ist **nicht transaktionsbewusst**; sie arbeitet direkt auf `_store`. Der neue Pfad darf daher nur aktiv werden, wenn **keine externe ADO.NET-Transaktion** eingeschrieben ist.

## Lösungsansatz

### 1. Engine-seitig: transaktionsfähige Prepared-Statement-Ausführung

`WalhallaPreparedStatement` erhält interne Überladungen, die eine `WalhallaSqlTransaction?` bzw. deren `ITransaction<byte[], byte[]>` akzeptieren:

- `internal WalhallaResultSet Execute(WalhallaSqlTransaction? transaction)`
- Falls `transaction != null`: anstelle von `_store.GetRow`/`ScanRowKeyRange`/… wird die transaktionale Sicht (`transaction.StorageTransaction`) verwendet, damit uncommitted Writes und Snapshot-Isolation sichtbar sind.
- Falls `transaction == null`: bestehendes Verhalten.

Diese Änderung ist Voraussetzung dafür, dass der ADO.NET-Provider den vorbereiteten Pfad auch innerhalb von `DbTransaction` verwenden kann, ohne Korrektheit zu verlieren.

### 2. ADO.NET-Provider: Prepared-Statement-Cache pro Command

In `WalhallaSqlDbCommand`:

- Neues Feld `private WalhallaPreparedStatement? _preparedStatement;` (wird bei Änderung von `CommandText` zurückgesetzt).
- `PreparedCommandTemplate` wird weiterhin im Auto-Prepare-Schritt gebaut. Zusätzlich wird aus dem Template der SQL-Text mit konsistenten Platzhaltern (`@p0`, `@p1`, …) rekonstruiert.
- Beim ersten Ausführen eines SELECTs mit parametrisierten Platzhaltern:
  1. `WalhallaEngine.Prepare(sqlWithPlaceholders)` aufrufen → liefert `WalhallaPreparedStatement` mit Plan-Cache-Hit/-Miss.
  2. Statement in `_preparedStatement` speichern.
- Bei jeder weiteren Ausführung:
  1. Parameterwerte aus der `DbParameter`-Collection in `_preparedStatement.Bind(index, value)` übertragen.
  2. `_preparedStatement.Execute(transaction)` aufrufen.
  3. Ergebnis über bestehenden `WalhallaSqlDbDataReader` / `ExecuteScalar`-Pfad zurückgeben.

Nicht-SELECT-Statements (INSERT/UPDATE/DELETE/DDL) sowie Virtual-Queries (`information_schema`) behalten den bestehenden Pfad über `SqlLiteralFormatter` und `WalhallaEngine.Execute(sql)`.

### 3. ADO.NET-Provider: Connection-Session-Pooling (InProcess, best effort)

In `WalhallaSqlDbConnection`:

- Statischer `ConcurrentDictionary<string, ConcurrentQueue<ISqlClientSession>> _sessionPool` pro Connection-String (bzw. Engine-Identität).
- `Open()` versucht zuerst, eine passende Session aus dem Pool zu entnehmen; erst wenn der Pool leer ist, wird `SqlClientSessionFactory.Create` aufgerufen.
- `Close()` legt die `ISqlClientSession` zurück in den Pool, sofern sie nicht in einem fehlerhaften Zustand ist (z. B. offene Transport-Transaktion). Der Engine-Lease wird weiterhin wie bisher freigegeben.
- Pool-Einträge werden bei Bedarf begrenzt (z. B. max. 8 pro Schlüssel) und bei Prozessende disposed.

Damit bleibt die Engine-Instanz weiterhin über `EmbeddedEngineRegistry`/`SharedInMemoryRegistry` geteilt, aber der Overhead für `new WalhallaSqlClientSession()` pro `Open()`/`Close()` entfällt.

### 4. Parameter-Normalisierung und Cache-Schlüssel

- Der Cache-Schlüssel für das prepared Statement auf Command-Ebene ist der **normalisierte SQL-Text** (ohne Literalwerte, mit konsistenten Platzhaltern).
- `BuildPreparedCommandTemplate` generiert bereits eindeutige Platzhalter (`p_{name}` für benannte, `p{index}` für positionale). Diese bleiben unverändert.
- `WalhallaEngine.Prepare()` cached den Plan unter `plan:{sql}:v{schemaVersion}`. Da der normalisierte SQL-Text jetzt stabil ist, entstehen Cache-Hits bei wiederholten Abfragen.

## Implementierungsschritte

1. ✅ **Engine:** `WalhallaPreparedStatement` transaktionsfähig machen.
   - Datei: `WalhallaSql/WalhallaSql/Api/WalhallaPreparedStatement.cs`
   - Hinzufügen: `internal WalhallaResultSet Execute(WalhallaSqlTransaction? transaction)` und Adapter für PK-Lookup, PK-Range, Index-Scan, ScanWithPredicateFirst.
   - **Hinweis:** Der transaktionsfähige Pfad ist vorbereitet; im aktuellen Stand wird der Prepared-Cache außerhalb von Transaktionen verwendet, um Korrektheit zu garantieren.

2. ✅ **ADO.NET Session:** `ISqlClientSession` erweitern.
   - Datei: `WalhallaSql/WalhallaSql.AdoNet/SqlClient/ISqlClientSession.cs`
   - Neue Methode `SqlExecutionResult ExecutePrepared(...)` vorhanden.

3. ✅ **ADO.NET InProcess-Session:** `WalhallaSqlClientSession` implementiert `ExecutePrepared`.
   - Datei: `WalhallaSql/WalhallaSql.AdoNet/SqlClient/WalhallaSqlClientSession.cs`
   - Bindet Parameterwerte an `WalhallaPreparedStatement` und führt `Execute()` aus.

4. ✅ **ADO.NET Command:** `WalhallaSqlDbCommand` nutzt Prepared-Cache.
   - Datei: `WalhallaSql/WalhallaSql.AdoNet/WalhallaSqlDbCommand.cs`
   - Feld `_preparedStatement` wird pro Command-Text gecacht; SELECTs ohne Transaktion laufen über `WalhallaEngine.Prepare()`.

5. ✅ **ADO.NET Connection:** Session-Pooling.
   - Datei: `WalhallaSql/WalhallaSql.AdoNet/WalhallaSqlDbConnection.cs`
   - Statischer `ConcurrentDictionary`-Pool (max. 8 Sessions pro Engine/Connection-String); `Open()`/`Close()` wiederverwenden InProcess-Sessions.

6. ✅ **Tests & Benchmarks (ADO.NET).**
   - Unit-Tests für wiederholte parametrisierte SELECTs in `WalhallaSql.EfCore.Tests/AdoNetPreparedStatementTests.cs` bestanden.
   - Join-Allokations-Benchmarks (Punkt 3) erfüllen die Akzeptanzkriterien.

7. ✅ **PgWire Prepared-Statement/Portal-Cache (neuer Punkt 2).**
   - Datei: `WalhallaSql/WalhallaSql.PgWire/PgWireServer.cs`
   - `HandleBind` kompiliert SELECTs über `WalhallaEngine.Prepare()` in `WalhallaPreparedStatement` und bindet dekodierte Parameterwerte.
   - `HandleExecute` verwendet das gecachte Prepared Statement außerhalb von Transaktionen; Transaktionen, Nicht-SELECTs und nicht-kompilierbare Statements fallen auf den bestehenden Literal-Rewrite-Pfad zurück.
   - Smoke-Test `ExtendedQuery_ParameterizedSelect_ReusesStatementAcrossExecutions` in `WalhallaSql.PgWire.Tests` bestanden.

## Akzeptanzkriterien

- `SelectRangeMaterialized`-Benchmark zeigt weniger Allocations pro Iteration.
- `WalhallaEngine.PlanCacheHits` steigt bei wiederholter Ausführung des gleichen parametrisierten SELECTs; `PlanCacheMisses` bleibt bei 1 pro Statement.
- ADO.NET-Testsuite bleibt grün (insbesondere Transaktionstests, PgWire-Tests, Prepared-Statement-Tests).
- Keine funktionale Änderung für DDL/DML/Virtual-Queries.

## Nicht im Scope

- Async/Pipeline-Optimierungen.
- Änderungen am SQL-Parser oder Query-Planner.
