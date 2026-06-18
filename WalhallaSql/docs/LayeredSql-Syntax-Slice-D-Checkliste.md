Stand: 28.03.2026

# LayeredSql Syntax Slice D

Ziel: Set-Operator-Syntax in den Syntax-Layer ziehen, den StatementMapper von lokalem UNION-/EXCEPT-/INTERSECT-Scanning entlasten und die Runtime-Aufrufe direkt auf `SqlExecutionParser` ausrichten.

## Umgesetzt

- `SqlSetOperatorSyntax` und `SqlCompoundSelectSyntax` in `LayeredSql.Syntax`
- `SqlCompositeSyntaxParser.TryParseCompoundSelect(...)` als Syntax-Entry fuer UNION/EXCEPT/INTERSECT
- `SqlStatementMapper` nutzt fuer Set-Operatoren jetzt den Syntax-Parser statt lokaler String-Splitting-Helfer
- `SqlStatementExecutor` und `SqlSyntaxLayerTest` greifen direkt auf `LayeredSql.Runtime.SqlExecutionParser` zu
- Laufzeitnahe Produktpfade in `InProcessSqlClientSession` und `SqlWorkbenchService` verwenden fuer ausfuehrbare SQL-Pfade jetzt ebenfalls direkt `SqlExecutionParser`
- Auch das Benchmarking fuer ausfuehrbare SQL-Pfade in `BenchmarkSuite1/SqlStatementExecutorHotPathBenchmark.cs` laeuft jetzt ueber `SqlExecutionParser`; direkte `ParseStatement(...)`-Aufrufe bleiben damit ausserhalb von Tests nur noch im Runtime-Gateway selbst.
- Die fruehere Prefer-/Fallback-Policy im `SqlExecutionParser` ist weiter ausgeduennt: `TOP`, `LIMIT/OFFSET`, `ORDER BY ... DESC` und kanonische SELECT-Formen laufen jetzt direkt ueber den Core; die alte DESC-Sonderbehandlung ist durch Core- und Runtime-Regressionen ersetzt.
- Der pauschale Runtime-Mapper-Fallback fuer `SELECT` ist entfernt; repraesentative JOIN-, UNION-, TOP-, DESC- und Subquery-Executorpfade laufen jetzt end-to-end direkt ueber `SqlExecutionParser`.
- `DROP VIEW`, `DROP TABLE` und `DROP INDEX` laufen im Runtime-Gateway jetzt nachweisbar ueber native Core-Pfade; die zugehoerigen Mapper-Fallback-Praefixe sind aus `SqlExecutionParser` entfernt.
- `CREATE VIEW` sowie die kanonischen `ALTER TABLE`-Unterfamilien (`ADD COLUMN`, `ADD FOREIGN KEY`, `ALTER COLUMN`, `DROP COLUMN`, `DROP CONSTRAINT`, `RENAME COLUMN`, `RENAME TO`) laufen im Runtime-Gateway jetzt ebenfalls nachweisbar ueber native Core-Pfade; auch diese Mapper-Fallback-Praefixe sind aus `SqlExecutionParser` entfernt.
- Kanonische `WITH`-Statements laufen im Runtime-Gateway jetzt ebenfalls ohne Mapper-Fallback; Executor-Regressionen fuer View- und ALTER-DLL-Pfade gehen dabei gezielt ueber `SqlExecutionParser`, und der EF-Generated-Key-Pfad deckt die produktnahe `ORDER BY ... DESC LIMIT 1`-Sequenzbestimmung jetzt ebenfalls explizit ab.
- Die verbleibenden Produktoberflaechen sind enger abgesichert: PgWire-Statement-Describe rewritet vorbereitetes `SELECT *` und `alias.*` jetzt ueber denselben bekannten Spaltenpfad wie Portal-Describe, liefert fuer DECIMAL/NUMERIC zudem konsistente OID-/pg_type-Metadaten bis in den Extended-Query-Datenpfad, und der EF-Custom-Translator hat nun embedded- sowie PgWire-Regressionen fuer `Include(...).ExecuteDelete()` plus direkte Dependent-`ExecuteDelete()`-Versuche auf Shared-Table-Zielen.
- `SqlRuntimeStatementParser` ist im aktiven Codepfad nicht mehr vorhanden; uebrig bleiben derzeit nur noch aeltere Dokumentationsverweise in den Slice-A/B/C-Checklisten.

## Guardrails

1. `dotnet build .\LayeredSql\LayeredSql.csproj -c Release`
2. `dotnet run --project .\LayeredSql\LayeredSql.csproj -c Release`
3. Auf gruen achten bei:
   - `SqlSyntaxLayerTest`
   - `SqlStatementMapperTest`
   - `SqlStatementExecutorTest`
