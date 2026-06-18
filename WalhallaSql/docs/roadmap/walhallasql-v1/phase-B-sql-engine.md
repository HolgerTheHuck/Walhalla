# Phase B — SQL-Engine-Vollständigkeit

**Ziel:** Alle SQL-Sprachlücken schließen, die WalhallaSql heute von Postgres/SQLite trennen — auf Parser-, Planner- und Executor-Ebene, mit Tests pro Feature und Postgres-Differential.

**Voraussetzung:** Phase A abgeschlossen (Tests grün, CI vorhanden).

**Exit-Kriterien**
- Alle Slices grün mit ≥ 20 Test-Cases pro Feature
- Postgres-Differential-Suite (gleiche Query auf echtem PG + WalhallaSql) auf neuen Features grün
- sqllogictest-Subset um die neuen Features erweitert

---

## Slices

### B.1 — CHECK-Constraints

> **Status: ✅ abgeschlossen.** 20 Tests in `WalhallaSql.Tests/CheckConstraintTests.cs` grün; volle Suite (221) + PgWire (4) ohne Regression. NULL-Handling per 3VL (NULL=unknown=nicht-verletzt), SQLSTATE 23514 über PgWire. `CREATE TABLE LIKE` wird vom Engine nicht unterstützt → Constraint-Inheritance-Szenario entfällt.

**Scope**
- Parser: `CHECK (expr)` in `CREATE TABLE`, `ALTER TABLE ADD CONSTRAINT`, `ALTER TABLE DROP CONSTRAINT`
- AST: `SqlCheckConstraint` (Name, Expr-AST)
- Executor-Hook in `SqlStatementExecutor.ApplyChanges` — Validierung vor jeder INSERT/UPDATE-Row
- Persistenz: `TableDefinition.CheckConstraints` mit AST-Serialisierung
- Fehler: `SQLSTATE 23514` (check_violation) Postgres-konform

**Test-Szenarien (≥ 15)**
- numeric range (`CHECK (age >= 0)`)
- string regex/LIKE (`CHECK (email LIKE '%@%')`)
- multi-column (`CHECK (end_date >= start_date)`)
- NULL-Handling per SQL-Spec (NULL = unknown = nicht-verletzt)
- ALTER TABLE ADD CONSTRAINT auf existierender Tabelle mit verletzenden Rows → Fehler
- DROP CONSTRAINT entfernt Validierung
- Constraint-Inheritance bei `CREATE TABLE LIKE`

**Files** — `WalhallaSql/Sql/Parser/*`, `WalhallaSql/Execution/SqlStatementExecutor.*`, `WalhallaSql.Tests/Engine/CheckConstraintTests.cs`

---

### B.2 — Window Functions *(größter Slice — in Sub-Slices)*

#### B.2.1 — Frame-Spec-Parser
`OVER (PARTITION BY ... ORDER BY ... ROWS|RANGE BETWEEN ... AND ...)`

> **Status: ✅ abgeschlossen.** 9 Tests in `WalhallaSql.Tests/WindowFrameTests.cs` grün; volle Suite 230 ohne Regression. AST: `SqlWindowFrame` + `SqlWindowFrameBound` (statt `SqlWindowSpec`); `SqlWindowCall.Frame` ergänzt. Parser: ROWS/RANGE/GROUPS, alle 5 Frame-Grenzen, Single-Bound-Kurzform (`ROWS n PRECEDING` = BETWEEN … AND CURRENT ROW), `WINDOW w AS (...)` + `OVER w`. Frame-Werte werden noch nicht in der Ausführung konsumiert (Ranking nutzt impliziten Whole-Partition-Frame) — Verbrauch folgt in B.2.3/B.2.4.

- AST: `SqlWindowSpec` (Partition, Order, FrameMode, FrameStart, FrameEnd)
- Frame-Modi: `ROWS`, `RANGE`, `GROUPS`
- Frame-Grenzen: `UNBOUNDED PRECEDING`, `n PRECEDING`, `CURRENT ROW`, `n FOLLOWING`, `UNBOUNDED FOLLOWING`
- Named windows: `WINDOW w AS (...)` + `OVER w`

#### B.2.2 — Ranking-Funktionen
`ROW_NUMBER()`, `RANK()`, `DENSE_RANK()`, `NTILE(n)`, `PERCENT_RANK()`, `CUME_DIST()`

> **Status: ✅ abgeschlossen.** `ROW_NUMBER`/`RANK`/`DENSE_RANK` bereits in B.1; in B.2.2 ergänzt: `NTILE(n)` (long, Bucket-Verteilung mit größeren ersten Buckets), `PERCENT_RANK()` (double, `(rank-1)/(rows-1)`), `CUME_DIST()` (double, Peer-bewusst). 8 Tests in `WalhallaSql.Tests/WindowRankingTests.cs` grün; volle Suite 238 ohne Regression. NTILE-Argument im AST als `SqlWindowCall.NTileBuckets`.

- Keine Frame-Spec relevant (immer impliziter Whole-Partition-Frame)
- Test gegen Postgres-Output auf identischer Datenmenge

#### B.2.3 — Aggregat-Windows
`SUM/AVG/COUNT/MIN/MAX OVER (...)`

- Frame-aware Aggregation
- Inkrementelle Berechnung wo möglich (Sliding-Window)

> **Status: ✅ abgeschlossen.** 11 Tests in `WalhallaSql.Tests/WindowAggregateTests.cs` grün; volle Suite 249 ohne Regression. AST: `SqlWindowFunctionType.Aggregate` + `SqlWindowCall.AggregateFunction`/`.AggregateArgument`. Parser erkennt `SUM/AVG/COUNT/MIN/MAX` mit `OVER` (Vorrang vor Plain-GROUP-BY-Aggregat) inkl. `COUNT(*)`. Engine: `ComputeAggregateWindowValues` mit `ResolveFrameBounds` — erster Konsument von `SqlWindowFrame`. Default-Frame: ohne ORDER BY = ganze Partition, mit ORDER BY = peer-aware Running (RANGE UNBOUNDED PRECEDING .. CURRENT ROW). ROWS-Modus voll unterstützt (PRECEDING/FOLLOWING-Offsets, UNBOUNDED, CURRENT ROW). **Limitierung:** RANGE/GROUPS unterstützen nur UNBOUNDED + CURRENT ROW (numerische Offsets → `WalhallaException`). _(SortPartition-Row-Order-Bug in B.2.5 behoben.)_

#### B.2.4 — Offset-Funktionen
`LAG(expr, n, default)`, `LEAD(expr, n, default)`, `FIRST_VALUE`, `LAST_VALUE`, `NTH_VALUE`

- Default-Wert-Handling
- IGNORE NULLS / RESPECT NULLS

> **Status: ✅ abgeschlossen.** 12 Tests in `WalhallaSql.Tests/WindowOffsetTests.cs` grün; volle Suite 261 ohne Regression. AST: `SqlWindowFunctionType.{Lag,Lead,FirstValue,LastValue,NthValue}` + `SqlWindowCall.{OffsetColumn,OffsetAmount,OffsetDefault,IgnoreNulls}`. Parser: Mehr-Argument-Parsing via `SplitTopLevel` (LAG/LEAD: expr[, n[, default]]; NTH_VALUE: expr, n); `IGNORE NULLS`/`RESPECT NULLS` zwischen Funktion und `OVER`. Engine: `ComputeOffsetValues` (LAG/LEAD, offset 0 = aktuelle Zeile, IGNORE NULLS zählt nur Nicht-NULL), `ComputeValueFunctionValues` + `SelectFrameValue` (frame-aware; LAST_VALUE liefert per Default-Frame die aktuelle Zeile). Default-Literal über `ParseLiteral` mit Spaltentyp konvertiert. _(SortPartition-Row-Order-Bug in B.2.5 behoben.)_

#### B.2.5 — Plan-Modell
- Neuer Operator `WindowOperator` zwischen Aggregation und Projektion in `SqlStatementExecutor`
- Sortierung pro `PARTITION BY ... ORDER BY` (wo nicht durch Input gewährleistet)
- Memory-Footprint dokumentieren — große Partitionen → Spill-to-Disk als v1.x-Backlog

> **Status: ✅ abgeschlossen.** Window-Auswertung aus `WalhallaEngine` in dedizierten `WindowFunctionEvaluator` (`internal static`, Entry `Compute(...)`) unter `WalhallaSql/Execution/Window/` extrahiert — alle 21 Window-Methoden verschoben; Engine ruft nur noch `WindowFunctionEvaluator.Compute(...)`. **Zwei Korrektheits-Bugs behoben:** (1) `SortPartition` sortierte zuvor nur die Zeilen und paarte sie danach mit den Original-Indizes neu → Wert↔Zeile desynchronisiert, sobald Speicher-Reihenfolge ≠ ORDER-BY-Reihenfolge; jetzt werden `(Index, Row)`-Tupel gemeinsam über `OrderByExecutor.CreateRowComparer` sortiert (Tie-Break auf Originalindex → deterministisches ROW_NUMBER). (2) `ApplyPostProcessing` schlug Window-Werte nach dem äußeren ORDER-BY positional über den Post-Sort-Index nach; jetzt wird die Permutation (`origIndex`) verfolgt und über `srcIdx` gemappt. 6 Tests, die das alte Verhalten kodierten, auf korrektes ascending-Verhalten (äußeres `ORDER BY`) umgestellt; 2 neue Regressionstests (`WindowValues_UnsortedInput_MapToCorrectRows`, `WindowValues_OuterOrderDescending_MapToCorrectRows`) mit nicht-vorsortierter Eingabe. Volle Suite 263 grün. **Memory-Footprint:** O(n)-Buffering der Partition im Speicher (Spill-to-Disk = v1.x-Backlog).

**Files** — `WalhallaSql/Execution/Window/WindowFunctionEvaluator.cs` (neu), `WalhallaSql/Execution/OrderByExecutor.cs` (`CreateRowComparer`-Factory), `WalhallaSql/Api/WalhallaEngine.cs` (`ApplyPostProcessing`-Fix, Methoden extrahiert), `WalhallaSql.Tests/WindowFrameTests.cs` + `WalhallaSql.Tests/AdvancedQueryTests.cs` (Tests).

---

### B.3 — Join-Algorithmen *(parallel mit B.2)*

**Scope** — Aktuell nur Hash-Join. Ergänzen:

- **Sort-Merge-Join** für vorgeordnete Inputs (vermeidet Hash-Build)
- **Nested-Loop-Join** als Fallback und für small-side-Optimierung (< 100 Rows)
- **Planner-Heuristik** in `JoinSelect`:
  - Cardinality-Schätzung aus Index-Statistiken (`ANALYZE` aus Phase C.7)
  - Cost-Modell: Hash bei mittlerer Größe, NL bei kleinem Build-Side, Merge bei sortierten Inputs
- **Telemetrie:** `join-input strategy=hash|merge|nested-loop|composite-primary-key` (`strategy=` ist bereits in Trace nach April-2026 B.2 — nur erweitern)

**Test-Szenarien**
- 1k × 1k → Hash bevorzugt
- 10 × 100k → NL mit small-side
- vorgeordneter Index-Range × vorgeordneter Index-Range → Merge
- Planner-Switch dokumentiert in EXPLAIN-Output

**Files** — `WalhallaSql/Execution/Join/SortMergeJoin.cs` (neu), `NestedLoopJoin.cs` (neu), `JoinPlanner.cs` (neu/erweitert)

**Sub-Slices**
- ✅ **B.3.1 — Hash-Join-Operator-Extraktion** *(abgeschlossen)*: Duplizierte Inline-Hash-Join-Step-Logik aus `WalhallaEngine.ExecuteJoinSelect` und `WalhallaPreparedStatement.ExecuteJoin` in gemeinsamen `WalhallaSql/Execution/Join/HashJoinOperator.cs` (`ExecuteStep`) gezogen. Toter `JoinExecutor.cs` + redundante `ListValueKeyComparer`/`JoinKeyComparer` entfernt. Kein Verhaltenswechsel (263/263 grün). Schafft die saubere Naht für Nested-Loop/Sort-Merge.
- ✅ **B.3.2 — Nested-Loop-Join** *(abgeschlossen)*: `NestedLoopJoin.ExecuteStep` (identische Semantik + Ausgabereihenfolge + NULL-Key-Verhalten wie Hash) + gemeinsamer `JoinKeyComparer`. Größenbasierte Strategie-Auswahl `JoinStrategySelector` (Build-Side ≤ `NestedLoopMaxBuildRows`=100 → Nested-Loop, sonst Hash; CROSS→Hash) über Dispatcher `JoinStepExecutor`, in beide Laufzeitpfade verdrahtet. Bestehende 15 Join-Tests laufen jetzt über den NL-Pfad; 3 neue Hash-Pfad-Tests (`JoinStrategyTests`, Tabellen >100 Zeilen). 266/266 grün. EXPLAIN-Strategie-Anzeige + Telemetrie bleiben B.3.4.
- ✅ **B.3.3 — Sort-Merge-Join** *(abgeschlossen)*: `SortMergeJoin.ExecuteStep` mergt zwei nach dem Join-Key vorsortierte Inputs (keine Hash-Allokation, kein Re-Sort). `JoinKeyOrderComparer` (totale Ordnung, konsistent zu `JoinKeyComparer`-Gleichheit, NULL zuerst). `JoinStepExecutor` wählt Sort-Merge nur, wenn beide Seiten via `IsSortedByKey` bereits nach ihrem Key geordnet sind (z. B. PK=PK-Joins) — dann ist Key-Reihenfolge == Eingabereihenfolge, also bit-identische Ausgabe + NULL-Semantik wie Hash/Nested-Loop. 4 neue `SortMergeJoinTests` (INNER/LEFT/RIGHT + Duplikat-Keys). 270/270 grün.
- ✅ **B.3.4 — Planner-Heuristik + Telemetrie** *(abgeschlossen)*: Geteilte Strategie-Entscheidung `JoinStepExecutor.ChooseStrategy` (Sort-Merge bei vorsortierten Inputs, sonst größenbasiert) als Single Source of Truth für Laufzeit-Dispatch. Plan-Zeit-Schätzer `JoinStrategyEstimator.Estimate` (Cardinality via `TableStore.CountRows`; Sort-Merge-Proxy = beide Join-Spalten sind PK). EXPLAIN ist jetzt strategie-bewusst: Operation-Label `HASH_JOIN`/`NESTED_LOOP_JOIN`/`SORT_MERGE_JOIN`/`CROSS_JOIN` + `strategy=…`-Annotation und `~LxR`-Schätzung in den Details (Planner-Switch dokumentiert in EXPLAIN). 1 neuer EXPLAIN-Test (PK=PK → Sort-Merge), bestehender Join-EXPLAIN-Test auf strategie-bewusste Ausgabe umgestellt. 271/271 grün.

> **Status: ✅ abgeschlossen.** Alle vier Join-Algorithmen (Hash, Nested-Loop, Sort-Merge) hinter `JoinStepExecutor` mit größenbasierter + ordnungsbewusster Strategie-Auswahl; strategie-bewusstes EXPLAIN. 271/271 grün.

---

### B.4 — JSON-Operatoren (Postgres-kompatibel)

**Scope**
- **Operatoren**
  - `->` (JSON-Member as JSONB)
  - `->>` (JSON-Member as Text)
  - `#>` (JSON-Path as JSONB)
  - `#>>` (JSON-Path as Text)
  - `@>` (Contains)
  - `<@` (Contained-By)
  - `?` (Has-Key)
  - `?|` (Has-Any-Key)
  - `?&` (Has-All-Keys)
- **Funktionen**
  - `jsonb_build_object`, `jsonb_build_array`
  - `jsonb_array_elements`, `jsonb_object_keys`
  - `jsonb_path_query`, `jsonb_path_exists`
  - `jsonb_set`, `jsonb_insert`, `jsonb_strip_nulls`
- **Index-Support:** GIN-Index auf JSONB → siehe [Phase C.5](phase-C-storage-mvcc.md#c5--gin-indizes-für-jsonb--volltext)

**Files** — `WalhallaSql/Sql/Functions/JsonFunctions.cs` (neu), `WalhallaSql/Execution/JsonOperators.cs` (neu)

**Sub-Slices**
- ✅ **B.4.1 — JSON-Core-Extraktion** *(abgeschlossen)*: Die inline im `WhereCompiler` eingebettete JSON-Pfad-Navigation (`ParseJsonPath`, `ExtractJsonPathValue`, Arrow-Auswertung) in einen frontend-neutralen `WalhallaSql/Execution/JsonOperators.cs` (`internal static`) gezogen. `WhereCompiler` delegiert jetzt für `->`/`->>` und `JSON_EXTRACT`/`JSON_VALUE` an `JsonOperators.ArrowValue`/`ParseJsonPath`/`ExtractJsonPathValue`. Kein Verhaltenswechsel (271/271 grün). Schafft die saubere Naht für die Postgres-Operatoren/Funktionen in B.4.2–B.4.4.
- ✅ **B.4.2 — Postgres-Arrow/Path-Operatoren** *(abgeschlossen)*: `->`/`->>` akzeptieren jetzt zusätzlich zum MySQL-`$.path` auch Postgres-Operanden — Einzelschlüssel (`Data->'x'`) und Ganzzahl-Array-Index (`Data->'tags'->>1`); chainbar. Neue Pfad-Array-Operatoren `#>`/`#>>` (`Data#>'{a,b,c}'`, `Data#>>'{a,name}'`) über `JsonOperators.ParsePostgresPathArray`. Modell: `SqlWhereJsonArrowExpression` um `SqlJsonPathKind` (Member/Path) erweitert; `JsonOperators.ArrowValue(..., isPathArray)` wählt den Parser. 4 neue Tests, bestehende `$.path`-Tests unverändert grün. 275/275 grün.
- ✅ **B.4.3 — Containment/Key-Existenz** (`@>`, `<@`, `?`, `?|`, `?&`) *(abgeschlossen)*: Operatoren als neue `SqlWhereExpression`-Subtypen (`SqlWhereJsonContainsExpression` + `SqlJsonContainmentOperator`, `SqlWhereJsonKeyExistsExpression` + `SqlJsonKeyExistsOperator`). Parser erkennt `@>`/`<@` (vor `<`-Vergleich), `?|`/`?&`/`?` (in dieser Reihenfolge, zweistellig vor einstellig). Runtime in `JsonOperators`: rekursives `ContainsElement` (Object/Array/Scalar), `JsonKeyExists` mit `TryGetProperty`/`AnyKeyExists`/`AllKeysExist` (Key-Spec via JSON-Array-String). Compiler: `Expression.Call` auf `JsonContains`/`JsonKeyExists`-Wrapper. 14 Tests (6 Containment + 8 Key-Existence). 293/293 grün.
- ✅ **B.4.4 — `jsonb_*`-Funktionen** *(abgeschlossen)*: **Scalar functions** über `BuildFunctionCall`-Dispatch: `JSONB_BUILD_OBJECT` (key-value pairs via `Utf8JsonWriter`), `JSONB_BUILD_ARRAY`, `JSONB_STRIP_NULLS` (rekursives Entfernen von null-Properties), `JSONB_SET`/`JSONB_INSERT` (Path-basierte JSON-Modifikation via `ParseJsonPath`), `JSONB_PATH_EXISTS`/`JSONB_PATH_QUERY` mit minimalem jsonpath-Evaluator (`$.key.subkey`, `$.key[*].subkey`, `$.key[0]`, `$[*]`). `SqlWhereFunctionCallExpression` als truthy-fähig im Parser (für `JSONB_PATH_EXISTS` in WHERE). 12 Tests (5 simple + 7 path-based). 300/300 grün. **Deferred:** Set-returning functions (`jsonb_array_elements`, `jsonb_object_keys`, multi-row `jsonb_path_query`) und vollständiger jsonpath (Filter, rekursiver Descent) → v1.x-Backlog.

---

### B.5 — Recursive CTEs

> **Status: ✅ abgeschlossen.** 8 Tests in `WalhallaSql.Tests/AdvancedQueryTests.cs` + 6 diagnostische in `WalhallaSql.Tests/DiagnosticCteJoinTests.cs` grün; volle Suite 317 ohne Regression.

**Scope** (implementiert)
- `WITH RECURSIVE name AS (anchor UNION ALL recursive_part) SELECT ...`
- Iterations-Limit über `WalhallaOptions.RecursiveCteMaxIterations` (default 1000) → Fehler `SQLSTATE 42P19`
- Executor: Postgres Worktable-Pattern (Anchor initial, dann Loop: Temp-Table ersetzen, rekursiven Member ausführen, neue Rows akkumulieren)
- UNION (Distinct) dedupliziert innerhalb der Iteration und gegen akkumulierte Rows; UNION ALL akkumuliert Duplikate
- CTE-Temp-Tables inferieren Spaltentypen aus Result-Werten (statt alles STRING)
- Non-Recursive-CTE-Support bleibt unverändert erhalten

**Änderungen**
- `SqlStatement.cs`: `SqlCteDefinition.Body` → `SqlStatement`, `SqlWithStatement.IsRecursive`
- `SqlStatementParser.cs`: `ParseWithStatement` mit `isRecursive`-Flag, `IsWithRecursive`-Helper, `Trim()`-Fix für Multi-Line-Compound-Selects
- `WalhallaOptions.cs`: `RecursiveCteMaxIterations` Property (default 1000, validation ≥ 1, freeze guard)
- `WalhallaEngine.cs`: `ExecuteWith`-Dispatch, `ExecuteRecursiveWith` (Worktable-Loop), `InferColumnTypes`/`InferScalarType` (Typ-Inferenz für CTE-Temp-Tables)
- `JoinKeyComparer.cs`: Cross-Type-Coercion für String↔Numeric in `Equals`, `CanOrder`, `Compare` (+ `TryCoerce`-Helper)
- `RowEqualityComparer`: Bestehende Equals-Logik (String case-insensitive, sonst value.Equals)

**Test-Szenarien** (8 Tests in AdvancedQueryTests + 6 in DiagnosticCteJoinTests)
- Tree-Traversal (parent-child, 4 Ebenen) ✅
- UNION ALL erhält Duplikate (multi-path Graph) ✅
- UNION dedupliziert ✅
- Empty Anchor → leeres Result ✅
- Single Iteration (Anchor-only) ✅
- Cycle Detection (UNION ALL mit Iterations-Limit 5) → SQLSTATE 42P19 ✅
- Konfigurierbares Limit (`RecursiveCteMaxIterations = 3`) ✅
- Main-SELECT-Filter nach Akkumulation ✅
- Non-Recursive-CTE mit JOIN (Typ-Koerzion) ✅
- Recursive-CTE ohne JOIN ✅
- Recursive-CTE mit JOIN (CTE auf rechter Seite) ✅
- Direkter JOIN gegen persistierte Tabelle (Kontroll-Test) ✅

**Deferred → v1.x**
- `CYCLE col SET cycle_mark TO 'y' DEFAULT 'n' USING path_col` (SQL:2016)
- Mutual Recursion über zwei CTEs
- Column-Name-List (`WITH RECURSIVE cte(col1, col2) AS (...)`)
- Mehrere rekursive CTEs in einem WITH RECURSIVE

**Files** — `WalhallaSql/Sql/SqlStatement.cs`, `WalhallaSql/Parsing/SqlStatementParser.cs`, `WalhallaSql/Api/WalhallaEngine.cs`, `WalhallaSql/Api/WalhallaOptions.cs`, `WalhallaSql/Execution/Join/JoinKeyComparer.cs`, `WalhallaSql.Tests/AdvancedQueryTests.cs`, `WalhallaSql.Tests/DiagnosticCteJoinTests.cs`

---

### B.6 — UPSERT (INSERT … ON CONFLICT + MERGE)

> **Status: ✅ abgeschlossen.** 18 ON CONFLICT Tests + 9 MERGE Tests grün; volle Suite 344 ohne Regression.

**Scope** (implementiert)
- **Postgres-Syntax:** `INSERT ... ON CONFLICT (col) DO UPDATE SET col = EXCLUDED.col`
- `ON CONFLICT DO NOTHING`
- `ON CONFLICT ON CONSTRAINT constraint_name`
- `ON CONFLICT (col) DO UPDATE SET ... WHERE condition` (conditional upsert)
- `EXCLUDED.col` pseudo-table in DO UPDATE SET assignments
- `INSERT ... SELECT ... ON CONFLICT ...` (both INSERT … VALUES and INSERT … SELECT support)
- Multi-value INSERT with ON CONFLICT (row-by-row processing)
- **SQL-Standard `MERGE`** (SQL:2008+): `MERGE INTO target USING source ON pred WHEN MATCHED THEN UPDATE SET ... WHEN NOT MATCHED THEN INSERT (...)`
- Source alias support (`USING S AS alias`)
- Trigger-Ordering: BEFORE INSERT → CONFLICT-Check → INSERT or UPDATE path with appropriate triggers
- PK + UNIQUE index conflict detection (both paths)

**Änderungen**
- `SqlStatement.cs`: `SqlConflictTarget`, `SqlConflictAction`, `SqlOnConflictClause` types; `OnConflict` property on `SqlInsertStatement`/`SqlInsertSelectStatement`
- `SqlStatementParser.cs`: `ParseInsert` extended for ON CONFLICT detection after VALUES/SELECT; `ParseOnConflict`, `ParseSelectWithOptionalOnConflict`, `TryParseOnConflict` helpers
- `WalhallaEngine.cs`: `ExecuteInsertRowByRow` (row-by-row ON CONFLICT processing); `ExecuteMerge` + `InsertMergeRow` + `GetSourceColumnValue` helpers; dispatch cases for `SqlMergeStatement`
- `PublicAPI.Unshipped.txt`: New types and properties

**Test-Szenarien** (18 ON CONFLICT + 9 MERGE Tests)
- ON CONFLICT DO NOTHING: SingleConflict, MixedRows, NoConflict, AllConflicts, TargetColumns, OnConstraint
- ON CONFLICT DO UPDATE: ExcludedCol, MultipleSetColumns, LiteralAssignment, MixedRows, WhereTrue, WhereFalse, NoTarget, UniqueIndexConflict
- INSERT … SELECT … ON CONFLICT: DoNothing, DoUpdate
- Trigger-Ordering: DoNothing fires BEFORE but not AFTER, DoUpdate fires UPDATE triggers
- MERGE: Matched→Update, NotMatched→Insert, Mixed, MultipleSetColumns, EmptySource, AffectedCount, UpdateTrigger, InsertTrigger, WithAlias

**Deferred → v1.x**
- MERGE with subquery/CTE source (only table source in v1)
- `WHEN MATCHED AND condition` / `WHEN MATCHED THEN DELETE`
- Multiple WHEN clauses
- Expression-based SET (`SET x = x + 1`)
- `ON CONFLICT DO UPDATE SET col = DEFAULT`
- Complex MERGE ON predicates (only PK equality supported)

**Files** — `WalhallaSql/Sql/SqlStatement.cs`, `WalhallaSql/Parsing/SqlStatementParser.cs`, `WalhallaSql/Api/WalhallaEngine.cs`, `WalhallaSql/PublicAPI.Unshipped.txt`, `WalhallaSql.Tests/OnConflictTests.cs`, `WalhallaSql.Tests/MergeTests.cs`

---

### B.7 — Prepared-Statement-Lifecycle-Hardening  ✅

**Status:** Completed (2026-06-01)

**Scope**
- **Plan-Cache für `Prepare()`** — Schema-Version-aware Cache-Key (`plan:{sql}:v{schemaVersion}`). Lookup vor QueryPlanner.Build. Gleicher `BoundedLruCache<CompiledPlan>` wie `ExecuteSelect()`.
- **Per-Table Schema-Version-Tracking** — `Dictionary<string, int> _schemaVersions` im Engine. `BumpSchemaVersion(tableName)` in allen DDL-Methoden (CREATE/DROP/ALTER TABLE, CREATE/DROP INDEX). Schema-Änderung → Key-Wechsel → natürlicher Cache-Miss via LRU.
- **Global Clear weiterhin aktiv** — `InvalidatePlanCache()` (Clear) wird parallel zu `BumpSchemaVersion()` aufgerufen für `ExecuteSelect()`-Pläne (raw-SQL-Keyed) und Edge Cases.
- **`WalhallaOptions.PlanCacheCapacity`** — Neue Property mit freeze-guard, default 10.000. Env-Var `WALHALLASQL_PLAN_CACHE_CAPACITY` hat Vorrang.
- **Telemetrie** — `PlanCacheHits` / `PlanCacheMisses` Properties (long, thread-safe via Interlocked).
- **Tests** — 9 Tests in `PlanCacheTests.cs`: Cache-Hit, Cache-Miss, DDL-Invalidation, Parameter-Stability, Concurrent-Access, Cache-Capacity-Eviction, Drop-Table-Clear, Cross-DDL-Recompilation, Telemetry-Accuracy.

**Files** — `WalhallaSql/Api/WalhallaEngine.cs`, `WalhallaSql/Api/WalhallaOptions.cs`, `WalhallaSql/Api/WalhallaPreparedStatement.cs`, `WalhallaSql/PublicAPI.Unshipped.txt`, `WalhallaSql.Tests/PlanCacheTests.cs`

---

## Verification (phasenübergreifend)

- **sqllogictest-Subset** in `WalhallaSql.SqlLogicTests` erweitern um Window/CTE/CHECK
- **Postgres-Differential**: Container mit Postgres 16, gleiche Query gegen beide, Output-Vergleich (Cardinality + Reihenfolge wo deterministisch)
- pro Feature ≥ 20 Cases

## Reihenfolge & Parallelisierbarkeit

```
B.1 (CHECK)           ─┐
B.3 (Joins)           ─┼─ parallel
B.4 (JSON)            ─┘
B.2 (Window)          ─── B.2.1 → B.2.2/B.2.3/B.2.4 parallel → B.2.5
B.5 (Recursive CTE)   ── nach B.2.1 (teilt Frame-/Iteration-Logik)
B.6 (UPSERT)          ─── unabhängig
B.7 (Plan-Cache)      ─── nach allen Feature-Slices (sieht alle neuen Statement-Typen)
```

## Geschätzte Slice-Anzahl

11 Slices (B.1, B.2.1–B.2.5, B.3, B.4, B.5, B.6, B.7).
