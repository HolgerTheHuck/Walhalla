# Phase C — Storage & Concurrency (kritischste Phase)

**Ziel:** MVCC/Snapshot-Isolation produktiv einführen, Crash-Recovery property-based absichern, echte ICU-Collations, GIN-Indizes für JSONB+Volltext, bessere Cardinality-Statistiken für den Planner.

**Voraussetzung:** Phase B abgeschlossen (B.1–B.7 ✅, Stand 2026-06-01, 353/353 Tests grün).

**Status-Update (2026-06-02):** Phase C ist vollständig abgeschlossen. MVCC-Core, Crash-Recovery, ICU-Collations, GIN-Indizes und das vollständige Statistik-/Planner-System (C.7.1–C.7.9) sind implementiert. 485 Tests grün auf net8.0/9.0/10.0. C.6 ist v1.x-Backlog. → **Übergang zu Phase D.**

**Exit-Kriterien**
- Snapshot-Isolation als Default, `READ COMMITTED` + `REPEATABLE READ` + `SERIALIZABLE` per Session konfigurierbar
- Crash-Recovery durch `WalhallaSql.CrashTests` mit ≥ 10k random-kill-Property-Runs validiert
- ICU-Collations: `de-DE-x-icu`, `en-US-x-icu`, `tr-TR-x-icu` (Turkish-I-Problem) als Smoke-Test
- GIN-Index auf JSONB-Spalte zeigt ≥ 10× Speedup vs. seq-scan auf `@>`-Query (10k Rows)
- `ANALYZE` aktualisiert Histogramme; Planner nutzt sie messbar (EXPLAIN-Output-Differenz)

---

## Slices

### C.0 — Pre-MVCC Thread-Safety Baseline *(blockiert C.2)*

**Ziel:** Bevor MVCC den Schreibpfad überlagert, muss die heutige Single-Writer-Engine unter Multi-Thread-Last und Multi-Prozess-Zugriff sauber definiert sein. Liefert die Grundlage, gegen die MVCC später diff-bar bleibt.

**Status (23.05.2026):** A–E.1 umgesetzt, E.2 vertagt (nach Storage-Sharding).

| Sub | Inhalt | Status |
|---|---|---|
| **A** | Multi-Thread-Regressionstests (`WalhallaSql.Tests/ConcurrencyTests.cs`, 7 Facts: parallele Inserts disjunkt/dedup, parallele DDL für Trigger/Procedure/View vs. DML, File-Lock-Kontrakt) | ✅ |
| **B** | Engine-Metadaten thread-sicher: `_views`, `_procedures`, `_compiledProcedures`, `_triggersByTable` unter `_metaSync` (Monitor), Snapshot-then-iterate beim Trigger-Fire um Deadlock mit Engine-RW-Lock zu vermeiden (`WalhallaSql/Api/WalhallaEngine.cs`, ~23 Sites) | ✅ |
| **C** | `WalLog._appendLock` auf den synchronen Append-Pfaden (`AppendBatch`, `Truncate`, `Dispose`); Async-Pfade bleiben unlocked, weil `GroupCommitQueue` sie serialisiert (`WalhallaSql/Storage/WalLog.cs`) | ✅ |
| **D** | File-Lock `<RootPath>/wal.lock` mit `FileShare.None` über die Engine-Lebensdauer; verhindert zwei Prozesse auf demselben Datenverzeichnis; bei InMemory deaktiviert (`WalhallaSql/Api/WalhallaEngine.cs`) | ✅ |
| **E.1** | Catalog-Lock von Data-Lock trennen: zweite `RowLockManager`-Instanz (oder `tableId=0` „catalog" vs. `tableId=1` „data") in `TableStore`, damit Katalog-Reader (`GetTableId`, `GetTableDefinition`, `GetTableIndexIds`) nicht mit Row-DML serialisieren. Lock-Ordering dokumentieren: catalog **vor** data. | ✅ umgesetzt — zweiter `RowLockManager _catalogLockManager` in `TableStore.cs`; 6 catalog-reads + 7 catalog-writes migriert; Lock-Ordering catalog→data; Regression-Test `CatalogRead_DoesNotBlockOnConcurrentDml` |
| **E.2** | *Vertagt nach C.2 + Storage-Sharding.* Echte parallele Writer auf disjunkten Tabellen verlangen entweder einen thread-safen B+Tree (Latch-Crabbing pro Page) oder per-Tabelle separierte B+Trees/ODS-Regionen. `BPlusTreeStore`/`BPlusTree`/`WTreeKeyValueStore`/`InMemoryStore` haben heute kein internes Locking — `TableWriteLock(0)` **ist** ihre einzige Serialisierungsbarriere. Naives Umschalten auf `tableId` würde die geteilte B+Tree-Struktur korrumpieren. | ⏸ vertagt |

**Exit-Kriterien**
- A–D dauerhaft grün (200/200 in `WalhallaSql.Tests` auf net9.0, Stand 23.05.2026)
- E.1 umgesetzt: paralleler `GetTableDefinition`/`GetTableId`-Mix gegen laufende `InsertRow`-Last blockiert nicht mehr (gezielter Test in `ConcurrencyTests.cs`)
- E.2 explizit in den C/S-Endzustand-Plan überführt (siehe Risiko-Notiz unten), kein Code-Lock-In, der per-Tabelle-Storage später verhindert
- Keine Regression in den bestehenden Trigger-/View-/Procedure-Pfaden

**Risiko-Notiz für C.2 und das C/S-Endziel**
- **MVCC ist mit Single-Writer kompatibel.** Klassische MVCC-Implementierungen (Postgres bis 9.x, SQLite-WAL) leben mit *einem* Writer + vielen Snapshot-Readern. C.2 darf und soll diesen Modus erst einmal liefern; Reader sehen Snapshots **ohne** TableLock, Writer serialisieren am bestehenden globalen Writer-Lock. Das löst den Haupt-Engpass (Reader blockiert Writer / Writer blockiert Reader) bereits vollständig.
- **C/S-Endziel bleibt erreichbar:** Der spätere Wechsel auf parallele Writer (E.2 + per-Tabelle-Storage) darf das MVCC-API nicht brechen. Konkrete Leitplanken für C.2-Design:
  - Snapshot-IDs/Versionen müssen pro Tabelle eindeutig adressierbar bleiben (kein global-monotoner Counter, der per-Tabelle-Sharding später hart macht — entweder `(tableId, version)` oder global, aber dann auch unter Sharding stabil).
  - Visibility-Checks und Garbage-Collection-Pfade dürfen **nicht** vom "alles im selben B+Tree"-Layout abhängen.
  - WAL-Tx-Records bleiben global serialisiert (`_appendLock` aus C); E.2 ändert nur den Pre-WAL-Schreibpfad, nicht das Log.
- `_appendLock` in `WalLog` bleibt auch unter MVCC erhalten (WAL ist global serialisiert); MVCC-Tx-Records dürfen darüber laufen, ohne die Lock-Disziplin zu brechen.

**Files** — `WalhallaSql/Api/WalhallaEngine.cs`, `WalhallaSql/Storage/WalLog.cs`, `WalhallaSql/Storage/TableStore.cs` (für E), `WalhallaSql.Tests/ConcurrencyTests.cs`

---

### C.1 — MVCC-Design-Spike *(blockiert C.2)*  ✅ (Code-first)

**Status:** Das Design wurde nicht als separates Dokument (`docs/architecture/mvcc.md`) festgehalten, sondern direkt in der `WTreeModern`-Library implementiert. Der Code IST das Design.

**Existierende WTreeModern-Assets (≈ C.1-Design-Entsprechung)**

| Konzept | Implementierung |
|---|---|
| Version-Chain-Layout | `WTreeModern/Tree/VersionedValue.cs` — Linked-List pro Row-Key: `(Sequence, IsTombstone, Value, Older)` |
| WTree-Erweiterung | `WTreeModern/Tree/LeafNode.cs` — `SortedList<TKey, VersionedValue<TValue>>`, `Apply()` mit Sequence-Nummern |
| Visibility-Algorithmus | `VersionedValue.TryGetVisible(ulong snapshotSeq, ...)` — Snapshot-konsistenter Read über Version-Chain |
| Vacuum-Strategie | `WTreeModern/Tree/BackgroundGC.cs` — Hintergrund-Thread, `PruneOldVersions(oldestSnapshot)` |
| WAL-Erweiterung | `WTreeModern/Operations/VersionedOperation.cs` — `(Sequence, OperationType, Key, Value)` Record |
| Transaction-Manager | `WTreeModern/Transactions/TransactionManager.cs` — Commit-Sequence, Write-Locks, Read-Locks, SSI-Detection |
| Isolation-Levels | `WTreeModern/Transactions/IsolationLevel.cs` — `Snapshot`, `ReadCommitted`, `Serializable` |
| Locking-Modus | `WTreeModern/Operations/Operation.cs` — Sequence=0 = Legacy-Mode (explizit markiert) |

**Was C.1 im Nachhinein leisten muss:**
- Brücken-Design dokumentieren: Wie kommen die WTreeModern-MVCC-Features in den WalhallaSql-Executor?
- Adapter-Migration: `WTreeStoreAdapter`/`WTreeTransactionAdapter` von Legacy (`sequence=0`) auf MVCC-Transaktionen umstellen
- SQL-Isolation-Level-Parsing: `SET TRANSACTION ISOLATION LEVEL ...` im Parser
- `VACUUM`-Statement: Parsing + Delegation an `BackgroundGC`

**Files** — `WTreeModern/Tree/VersionedValue.cs`, `WTreeModern/Tree/LeafNode.cs`, `WTreeModern/Transactions/Transaction.cs`, `WTreeModern/Transactions/TransactionManager.cs`, `WTreeModern/Tree/BackgroundGC.cs` (existieren bereits)

---

### C.2 — MVCC-Implementierung *(mehrere Sub-Slices)*

**Kern-Lücke:** Der MVCC-Core existiert vollständig in `WTreeModern`, aber die Brücke zur `WalhallaSql`-SQL-Engine fehlt. `WTreeStoreAdapter` + `WTreeTransactionAdapter` nutzen ausschließlich den Legacy-Pfad (`sequence=0`). Kein `WalhallaSql/Transactions/`-Verzeichnis, kein SQL-Isolation-Level-Parsing, kein `VACUUM`.

#### C.2.1 — Version-Chain im Storage-Layer  ✅ (WTree-Ebene)

- `WTreeModern/Tree/VersionedValue.cs` — Linked-List pro Row-Key: `(Sequence, IsTombstone, Value, Older)`. `TryGetVisible(snapshotSeq)`, `Push()`, `Prune()`.
- `WTreeModern/Tree/LeafNode.cs` — `SortedList<TKey, VersionedValue<TValue>>`. `Apply(operations)` mit Sequence-Nummern, `TryGetValue(key, snapshotSeq)`, `PruneOldVersions(oldestSnapshot)`.
- **Fehlt:** Unit-Tests spezifisch für Insert/Update/Delete-Versionsketten auf Storage-Ebene.

#### C.2.2 — Snapshot-Reader  ✅ (2026-06-01)

- `WTreeModern/Tree/WTree.cs` — `TryGetWithSnapshot(TKey, ulong, out TValue)`, `TryGetWithSnapshotReadOnly(...)`, `BeginTransaction(IsolationLevel)`.
- **Umgesetzt:**
  - `WalhallaSqlTransaction` hält optionalen `ITransaction<byte[], byte[]>` (via `SetWTreeTransaction`), Disposal kaskadiert.
  - `WalhallaEngine.BeginTransaction()` erstellt WTree-`ITransaction` sofort (nicht erst bei Commit).
  - `CommitTransaction()` nutzt existierende WTree-Transaktion wieder; Retry bei Conflict.
  - `RollbackTransaction()` rolled back + disposed WTree-Transaktion.
  - PK-Point-Lookup (`ExecuteSelect` Fast-Path) und Index-Scan-Pfad nutzen `_store.GetRow(tableId, pkValue, wTreeTx)` für Snapshot-Reads.
  - `MergeTransactionWrites`: Guard gegen projizierte Queries ohne PK-Spalte.
  - `SnapshotReadTests.cs`: 9 Tests (CommittedData, ReadYourOwnInserts/Updates, DeleteHidesRow, RollbackDiscardsChanges, DisposeRollsBack, ParallelTransactionIsolation, PkLookupUsesSnapshot, CommitPersistsWrites).
  - Scans bleiben auf Legacy-Pfad (WTree `FlushAll()` hat keinen Snapshot-Parameter — kommt mit C.8 MVCC-B+Tree).

#### C.2.3 — Write-Pfad mit Isolation-Levels  ✅

- `WTreeModern/Transactions/Transaction.cs` — WriteSet/ReadSet, Isolation-Levels (`Snapshot`, `ReadCommitted`, `Serializable`), Write-Write-Conflict, SSI-Read-Write-Conflict, `Commit()`/`Rollback()`.
- `WTreeModern/Transactions/TransactionManager.cs` — `AcquireCommitSequence()`, `AcquireTxId()`, `AcquireSnapshot()`, Write-Lock-Table, Read-Lock-Table (für SSI), `OldestActiveSnapshot`.
- `WTreeModern/Transactions/TransactionConflictException.cs` — Exception für aborted-conflicting Transactions.
- **Umgesetzt:** `SET TRANSACTION ISOLATION LEVEL ...` Parsing im `SqlStatementParser` (`SqlSetTransactionStatement`).
- **Umgesetzt:** Isolation-Level-Wiring im SQL-Executor (`WalhallaEngine._defaultIsolationLevel`, `BeginTransaction()` nutzt konfigurierbares Level, Retry-Pfad in `CommitTransaction()`).
- **Umgesetzt:** `WalhallaSqlTransaction.WTreeIsolationLevel` / `SetIsolationLevel()` für Retry-Pfad.
- **Bugfixes:** `TableStore.ApplyBatch` (WTree-Pfad) invalidiert jetzt `_rowByKey`- und `_rowCache`-Caches (UPDATE-Verlust nach Commit). `TransactionManager`-Dictionaries nutzen strukturelle `byte[]`-Equality (Write-Write-Conflict-Detection war durch Reference-Equality gebrochen). Neues `ByteArrayEqualityComparer` in WTreeModern.
- **Tests:** 11 Tests in `IsolationLevelTests.cs` (Snapshot/ReadCommitted/RepeatableRead/Serializable, Invalid-Level, Case-Insensitive, Inside-Transaction, Write-Conflict-Detection, Retry).

#### C.2.4 — Vacuum-Background-Worker  ✅

- `WTreeModern/Tree/BackgroundGC.cs` — Hintergrund-Thread, `PruneAllCachedLeaves()` mit `OldestActiveSnapshot`, konfigurierbares Intervall (default 30s).
- `WTreeModern/Tree/WTree.cs` — `StartBackgroundGC(TimeSpan?)`, `PruneAllCachedLeaves()` (beide public).
- **Umgesetzt:** `VACUUM [table_name]`-Statement (`SqlVacuumStatement`, Parsing + Execution).
- **Umgesetzt:** `TableStore.Vacuum()` — `FlushAll()` → `PruneAllCachedLeaves()` → `Commit()` unter `TableWriteLock(0)`.
- **Umgesetzt:** `WalhallaEngine.Vacuum(string?)`, Dispatch in beiden Execute-Pfaden.
- **Umgesetzt:** `VACUUM FULL`-Ablehnung mit `NotSupportedException`.
- **Tests:** 10 Tests in `VacuumTests.cs`.

#### C.2.5 — Locking-Modus als opt-in  ✅

- **Umgesetzt:** `TransactionMode` enum (`WalhallaSql/Core/TransactionMode.cs`) — `Locking = 0`, `Mvcc = 1`.
- **Umgesetzt:** `WalhallaOptions.TransactionMode?` Property (nullable, default `null` = auto-detect).
- **Umgesetzt:** `WalhallaEngine.UseMvcc` computed property — `true` wenn `TransactionMode == Mvcc` oder (`null` und `StorageMode == WTree`). 8 Branch-Sites von `_options.StorageMode != WTree` auf `!UseMvcc` umgestellt.
- **Umgesetzt:** `SET walhalla.transaction_mode = 'locking'|'mvcc'` Parsing (`SqlSetTransactionModeStatement`, `ParseSetTransactionMode()`), Engine-Dispatch mit Validierung (kein MVCC auf Non-WTree, kein SET in aktiver Transaktion).
- **Umgesetzt:** `WalhallaSql.Tests/TransactionModeTests.cs` — 19 Tests (Options, Locking-Mode, MVCC-Mode, SQL-Parsing, Kompatibilität).
- **Ergebnis:** 402/402 Tests grün (net8.0/net9.0/net10.0).

**Files** — `WalhallaSql/Core/TransactionMode.cs` (neu), `WalhallaSql/Api/WalhallaOptions.cs`, `WalhallaSql/Api/WalhallaEngine.cs`, `WalhallaSql/Sql/SqlStatement.cs`, `WalhallaSql/Parsing/SqlStatementParser.cs`, `WalhallaSql/PublicAPI.Unshipped.txt`, `WalhallaSql.Tests/TransactionModeTests.cs` (neu)

---

### C.3 — Crash-Recovery-Hardening  ✅ (2026-06-01)

**Status:** Neue Crash-Test-Suite in `WalhallaSql.CrashTests/` (17 Tests), die direkt auf `WalhallaSql.WalhallaEngine` mit `StorageMode.WTree` aufsetzt.

**Umgesetzt:**
- `WalhallaSql.CrashWorker/` — Subprozess-Konsolen-App: `<path> <committed> <dirty> [--locking] [--no-create]`, schreibt committed/dirty Records, crasht via `Environment.FailFast`.
- `WalhallaSql.CrashTests/CrashRecoveryTests.cs` — 15 deterministische Tests (5 Legacy-Szenarien portiert × 2 Modes + 5 MVCC-spezifisch):
  - Portiert: Baseline_CleanExit, CrashAfterDirtyWrite, MultiRun_PreviousCommitsIntact, SuccessiveCrashes, LargeCommittedBatch_SurvivesCrash (je für MVCC + Locking)
  - MVCC-spezifisch: Crash_DuringVacuum, Crash_DuringVersionChainBuild, Crash_AfterLargeVersionChain, Crash_DuringMixedWorkload, Crash_WithBackgroundGC
- `WalhallaSql.CrashTests/CrashPropertyTests.cs` — 2 FsCheck Property-Tests (`MaxTest = 100`):
  - `AllCommittedSequences_SurviveCrash` — zufällige INSERT/UPDATE/DELETE-Sequenzen überleben Crash
  - `DirtySequences_AreAbsentAfterCrash` — uncommitted Ops nach Crash garantiert nicht sichtbar
- **Bugfixes (während C.3 entdeckt):**
  - `ExecuteInsertBuffered`/`ExecuteInsertSelectBuffered`: BIGINT PK RowIdAlias nicht beachtet → Row-IDs aus PK-Werten extrahiert
  - `BufferInsert`: `_deletedRows` nicht bereinigt → Delete-then-Insert in gleicher Tx verlor Sichtbarkeit
- **Regression:** WalhallaSql.Tests 402/402 grün (net8.0/net9.0/net10.0), keine Regressionen.

**Nicht umgesetzt (niedrigere Priorität):**
- 10k-Run CI-Job (Nacht) — FsCheck `MaxTest = 10000` für Soak-Testing
- Crash während VACUUM mit BackgroundGC-Interleaving

---

### C.4 — ICU-Collations  ✅ (2026-06-01)

**Status:** Vollständig implementiert ohne externe NuGet-Dependency — nutzt `System.Globalization.CompareInfo` aus dem BCL. `-x-icu`-Suffix wird vor `GetCompareInfo()` gestrippt.

**Umgesetzt:**
- `CollationManager` (`WalhallaSql/Collation/CollationManager.cs`) — `ConcurrentDictionary<string, CompareInfo?>` Cache, Fast-Path: `"C"`/`null` → `OrdinalIgnoreCase`. Statische Methoden: `Compare`, `Equals`, `GetHashCode`.
- `ColumnCollationContext` (`WalhallaSql/Collation/ColumnCollationContext.cs`) — `CompareInfo?[]` pro Column-Index, `IsDefault`-Flag für Fast-Path, `Build(SqlTableDefinition)`.
- `SqlColumnDefinition` um `string? Collation = null` erweitert (positional record, backward-compatible).
- Parser: `COLLATE` in `CREATE TABLE`, `ALTER TABLE ADD COLUMN`, Expression-`COLLATE` (`SqlWhereColumnExpression.Collation`), `ORDER BY COLLATE` (`SqlOrderByColumn.Collation`).
- Binary-Persistenz (`TableStore.cs`): `[collationLen:1][collation:UTF8*]` nach den 4 fixed bytes pro Column. `collationLen=0` = keine Collation. Backward-compatible mit alten Katalogen.
- RENAME COLUMN erhält Collation via `c with { Name = alter.NewColumnName! }`.
- Alle 15+ Comparison-Sites auf `CollationManager` umgestellt: WHERE, JOIN (Hash + Sort-Merge), ORDER BY, GROUP BY, Window PARTITION BY, Aggregate Executor, Prepared Statements.
- `JoinKeyComparer`/`JoinKeyOrderComparer` nicht mehr Singleton — `CompareInfo?` als Constructor-Parameter.
- `CompiledPlan.CollationContext` lazy-built aus `TableDefinition`.
- `RawStringRef.CompareTo(string, CompareInfo?)` für Collation-aware Vergleiche ohne String-Decode im Fast-Path.
- PgWire: `pg_collation` Virtual Table (4 Rows: C/de-DE/en-US/tr-TR), `GetCollationOid`-Mapper, `attcollation` pro Column, `typcollation=100` für text/varchar, `datcollate`/`datctype` konfigurierbar.
- **Tests:** 21 Tests in `CollationTests.cs` (CollationManager, Schema/DDL, Turkish-I-Problem, German-Umlaut-Sorting, PgWire-Integration, Backward-Compatibility). 423/423 Tests grün, keine Regressionen.

**Deferred:**
- `CompareOptions.IgnoreCase` vs `None` — aktuell `CompareOptions.None` (case-sensitive für benannte Collations, `OrdinalIgnoreCase` für null). Design-Entscheidung kann später revisitiert werden.
- Per-Column-Collation aus `ColumnCollationContext` durch ORDER BY/GROUP BY/WHERE threaden — aktuell fließt nur null (OrdinalIgnoreCase) durch die Data-Plane.

---

### C.5 — GIN-Indizes für JSONB + Volltext  ✅ (2026-06-02)

**Status:** Vollständig implementiert für JSONB-Spalten mit `@>`, `?`, `?|`, `?&` Operatoren. Inverted-Index-Backfill während `CREATE INDEX`, Planner-Scoring (GIN bevorzugt bei JSONB-Prädikaten), InMemory-Scan mit binärer Suche.

**Umgesetzt:**
- `GinElementExtractor` (`WalhallaSql/Execution/GinElementExtractor.cs`) — Tokenisiert JSON-Strings in Key-Path- und Key=Value-Tokens.
- `GinIndexLookup` (`WalhallaSql/Execution/GinIndexLookup.cs`) — `@>`, `?`, `?|`, `?&` über Inverted-Index-Intersektion/Union.
- `SqlIndexType.Gin` in `SqlIndexDefinition` (`WalhallaSql/Sql/SqlIndexDefinition.cs`).
- Parser: `CREATE INDEX ... USING GIN (col)` wird erkannt (`WalhallaSql/Parsing/SqlStatementParser.cs`).
- `IndexSelector.SelectBestIndex` (`WalhallaSql/Execution/IndexSelector.cs`) — GIN-Indizes erhalten höhere Scores als BTree bei JSONB-Prädikaten.
- `TableStore.ScanIndex` (`WalhallaSql/Storage/TableStore.cs`) — InMemory-Pfad mit binärer Suche auf `_sortedKeys`.
- Backfill während `CREATE INDEX`: `ScanWithPredicate` → `GinElementExtractor` → `InsertIndexEntries`.
- Tests: `WalhallaSql.Tests/GinIndexTests.cs` — 10 Tests (Contains, Exists, AnyExists, AllExist, MultipleMatches, NoMatch, QueryPlannerPrefersGin, IndexNotUsedForNonJsonPredicate, Backfill, UpdateInvalidatesIndex).
- **Bugfixes während C.5:** `AggregateExecutor.ExecuteGroupBy` wurde nicht für Aggregate ohne GROUP BY aufgerufen (Routing-Bug in `WalhallaEngine` + PreparedStatement). `GroupKey.GetHashCode()` war für leere Arrays nicht deterministisch (`HashCode` nutzt Random-Seed). `COUNT(*)`/`COUNT(col)` geben jetzt `long` statt `int` zurück (PostgreSQL-Semantik). `PublicAPI.Unshipped.txt` + `SqlCreateIndexStatement` auf optionalen 5-Parameter-Record umgestellt; Analyzer-Warnings RS0016/RS0017/RS0026 aktiviert.
- **Regression:** WalhallaSql.Tests 436/436 grün (net8.0/net9.0/net10.0), keine Regressionen.

**Nicht umgesetzt (v1.x-Backlog):**
- Volltext-Indizierung (TSVector/TSQuery) — aktuell nur JSONB.
- `jsonb_path_ops` vs. `jsonb_ops` — aktuell nur ein einziger Extraktionsmodus (Key-Path + Key=Value).
- GIN auf Array-Spalten (`?`, `?|`, `?&` für Arrays sind im Parser vorhanden, aber `GinElementExtractor` behandelt aktuell nur JSONB).

---

### C.6 — GIST-Indizes *(optional, v1.x-Backlog)*

**Scope** — Räumliche Daten (`Geometry`-Datentyp existiert bereits) + Range-Typen.

**Entscheidung:** für v1 zurückstellen, wenn C.5 ausreicht. Sonst:
- Generalized Search Tree als baumartige Index-Struktur
- Operatoren: `&&` (overlap), `<<` (left-of), `@>` (contains), `&<` (overlaps-or-left)

**Files** — `Walhalla.Indexes/GistIndex.cs` (neu)

---

### C.7 — Statistiken & kostenbasierter Planner *(letzter offener v1-MUSS)*  ✅ C.7.1–C.7.9 vollständig

**Scope** — `ANALYZE` baut Histogramme + Häufigkeitsstatistiken pro Spalte auf; ein `SelectivityEstimator` speist Index-Wahl, Join-Strategie und EXPLAIN mit echten Kardinalitäts-Schätzungen statt roher Zeilenzahlen.

**Detailplanung:** → [phase-C7-statistics-detail.md](phase-C7-statistics-detail.md) (9 Sub-Slices C.7.1–C.7.9, Formeln, Exit-Kriterien, Risiken).

**Kurzfassung der Sub-Slices**
- C.7.1 Statistik-Datenmodell + In-Memory-Katalog
- C.7.2 `ANALYZE`-Statement (Parse + Build, mirror VACUUM)
- C.7.3 `SelectivityEstimator` (pure, voll testbar)
- C.7.4 Index-Wahl selektivitätsbasiert (Tie-Breaker in `IndexSelector`)
- C.7.5 Join-Strategie kardinalitätsbasiert (`JoinStrategyEstimator`)
- C.7.6 EXPLAIN `est_rows` — beweist Exit-Kriterium (Output-Differenz vor/nach ANALYZE)
- C.7.7 Persistenz im Katalog (restart-fest, backward-compatible)
- C.7.8 PgWire `pg_stats`-Virtual-Table
- C.7.9 Telemetrie + Bench + Doku

**Files** — `WalhallaSql/Statistics/*` (neu), `WalhallaSql/Execution/{IndexSelector,QueryPlanner}.cs`, `WalhallaSql/Execution/Join/JoinStepExecutor.cs`, `WalhallaSql/Parsing/SqlStatementParser.cs`, `WalhallaSql/Sql/SqlStatement.cs`, `WalhallaSql/Api/WalhallaEngine.cs`, `WalhallaSql/Storage/TableStore.cs`

---

### C.8 — MVCC-nativer B+Tree *(nach C.2, mittelfristig)*

**Motivation:** Der WTreeModern (B-Epsilon-Tree) hat einen strukturellen Range-Scan-Nachteil: `FlushAll()` traversiert vor jedem Scan *alle* internen Knoten (O(tree-size)), weil Schreiboperationen in internalen Knoten gepuffert werden und vor dem Lesen in die Blätter kaskadieren müssen. Ein B+Tree hat diesen Overhead nicht — Daten liegen immer direkt in den Blättern, Scans traversieren nur O(log N + matching-leaves) dank Subtree-Pruning via Separator-Keys.

**Kernidee:** Einen B+Tree von Grund auf mit MVCC bauen, statt MVCC auf den bestehenden in-place-update B+Tree draufzusetzen. Die MVCC-Infrastruktur aus WTreeModern wird wiederverwendet:

- **TransactionManager** (`WTreeModern/Transactions/TransactionManager.cs`) — Commit-Sequence, Snapshot-Verwaltung, Write/Read-Lock-Tables, SSI-Detection. Backend-agnostisch.
- **VersionedValue<T>** (`WTreeModern/Tree/VersionedValue.cs`) — Linked-List pro Key: `(Sequence, IsTombstone, Value, Older)`. `TryGetVisible(snapshotSeq)`, `Push()`, `Prune()`.
- **IsolationLevel** (`WTreeModern/Transactions/IsolationLevel.cs`) — `Snapshot`, `ReadCommitted`, `Serializable`.
- **BackgroundGC** — `PruneOldVersions(oldestSnapshot)` auf Leaf-Ebene.

**Was neu gebaut wird (nur die Baumstruktur):**

- **`Walhalla.Storage/MvccBPlusTree.cs`** — B+Tree mit:
  - Klassische B+Tree-Struktur (interne Knoten mit Separator-Keys, Blätter mit Key-Ranges)
  - `VersionedValue<T>` pro Key in den Blättern
  - Subtree-Pruning bei Range-Scans via Separator-Key-Vergleich (wie aktueller B+Tree)
  - Doubly-linked leaf chain für Forward/Backward-Scans
  - Kein `FlushAll`, keine internen Puffer — jeder Write geht direkt ins Blatt (O(log N))
  - Page-Latching (pro Page `ReaderWriterLockSlim`) für Baum-interne Concurrency
  - Bulk-Upsert-Pfad für Batch-Inserts (mehrere Writes in einer Page-Transaction)

- **Adapter** — `WalhallaSql`-kompatibles `IKeyValueStore`, nutzt MVCC-Transaktionen.

**Tradeoff vs. WTreeModern:**

| Aspekt | WTreeModern (B-Epsilon) | MVCC-B+Tree |
|---|---|---|
| Write (pro Key) | amortisiert O(log N / B) I/Os (Puffer) | O(log N) I/Os (direkt ins Blatt) |
| Range-Scan-Startup | O(tree-size) — `FlushAll` | O(log N) — Subtree-Pruning |
| Range-Scan pro Key | `TryGetVisible` (Version-Chain) | `TryGetVisible` (identisch) |
| Insert-Batch | Exzellent (Puffer kaskadieren) | Gut (Page-Batching, aber jeder Key geht ins Blatt) |
| MVCC-Infrastruktur | ✅ existiert | ✅ wiederverwendet |
| Baum-interne Concurrency | Per-Node-Latching | Per-Page-Latching |
| Produktiver Status | Teilweise (MVCC-Core fertig, SQL-Brücke fehlt) | Konzept |

**Ziel:** Zwei Backends mit identischer MVCC-Semantik, wählbar nach Workload:
- `StorageMode.WTree` → Write-Heavy, Batch-Inserts
- `StorageMode.MvccBPlusTree` → Read-Heavy, viele Range-Scans, OLAP

**Timing:** Nach Abschluss von C.2 (WTreeModern→SQL-Brücke). Der WTreeModern liefert das funktionierende MVCC-End-to-End; der MVCC-B+Tree optimiert danach den Read-Pfad.

**Files** — `Walhalla.Storage/MvccBPlusTree.cs` (neu), `WalhallaSql/Storage/MvccBPlusTreeStore.cs` (neu), Wiederverwendung: `WTreeModern/Tree/VersionedValue.cs`, `WTreeModern/Transactions/TransactionManager.cs`

---

## Verification (phasenübergreifend)

- **MVCC-Anomalie-Suite** (Berenson/Adya — Dirty-Read, Lost-Update, Phantom, Write-Skew, etc.) — Property-Tests
- **Postgres-Differential** auf MVCC-Verhalten in REPEATABLE READ + SERIALIZABLE
- **Soak-Test** in `WalhallaSql.CrashTests`: 1h-Lauf mit Mixed-Workload + alle 30 s `kill -9`, danach Konsistenz-Check
- **GIN-Speedup** dokumentiert in `docs/perf/gin-index.md`

## Aktueller Stand & Nächste Schritte

```
C.0 (Thread-Safety; A–E.1 ✅, E.2 ⏸) ─── BLOCKER für C.2 (erfüllt)
   │
   ├─ C.1 (Design-Spike) ✅ ─── Code-first in WTreeModern umgesetzt
   │
   ├─ C.2.1 (Version-Chain) ✅ ─── WTreeModern/Tree/VersionedValue.cs
   │
   ├─ C.2.2 (Snapshot-Reader) ✅ ─── PK/Index-Point-Reads via WTree-ITransaction
   │
   ├─ C.2.3 (Write-Pfad) ✅ ─── MVCC-Schreibpfad mit Isolation-Levels
   │
   ├─ C.2.4 (Vacuum) ✅ ─── SQL VACUUM + BackgroundGC-Bridge
   │
   ├─ C.2.5 (Locking-Mode) ✅ ─── TransactionMode Enum + SET + 19 Tests
   │
   │   ├─ C.3 (Crash-Recovery) ✅ — 17 Tests + FsCheck Properties, direkt auf WTree
   │   ├─ C.4 (Collations) ✅
   │   ├─ C.5 (GIN)        ✅ — 10 Tests, JSONB @>/?/?|/?&
   │   ├─ C.6 (GIST)       ❌ (v1.x)
   │   ├─ C.7 (Statistiken) ✅ — C.7.1–C.7.9 vollständig, 485 Tests grün
   │   └─ C.8 (MVCC-B+Tree) 🔮 — Konzept, nach C.2
```

## Geschätzte Slice-Anzahl

12 Slices (C.0, C.1, C.2.1–C.2.5, C.3, C.4, C.5, C.7, C.8) + C.6 v1.x.
**Davon abgeschlossen: C.0, C.1, C.2.1, C.2.2, C.2.3, C.2.4, C.2.5, C.3, C.4, C.5, C.7 (12/12 v1-Slices). C.7.1–C.7.9 vollständig abgeschlossen (485 Tests grün).**
**Phase C vollständig → Übergang zu Phase D (Embedded v1.0 Release).**

## Hauptrisiko

**Performance-Regression auf Schreibpfad durch MVCC** ist die größte Einzelgefahr. Mitigation: C.2.5 (Locking-Modus opt-in) bleibt vollständig produktiv; jeder MVCC-Sub-Slice mit BenchmarkDotNet-Vorher/Nachher; Abort-Kriterium > 2× Latenz-Regression auf Standard-Schreibworkload → Re-Design.
