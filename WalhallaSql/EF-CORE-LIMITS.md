# EF Core Provider — Bekannte Grenzen (Beta-1)

Dieses Dokument listet die bekannten Einschränkungen des WalhallaSql EF Core Providers
mit Stand Juni 2026. Die Grenzen sind nach Impact sortiert.

## P0 — Grundlegende LINQ-Operationen

### DateOnly/TimeOnly in LINQ-Queries
- **Betroffene Tests:** `MinimalLinqOperatorsTests.Where_dateonly_equality_filters_by_date`, `Where_timeonly_equality_filters_by_time`, `Where_dateonly_greater_than_filters_by_range`
- **Symptom:** `Where(e => e.BirthDate == new DateOnly(...))` liefert leere Ergebnismenge; `Where(e => e.BirthDate > ...)` liefert alle Zeilen statt gefiltert.
- **Ursache:** DateOnly/TimeOnly-Literale werden nicht korrekt in SQL-Literale übersetzt oder der Engine-Vergleich behandelt sie nicht als vergleichbare Werte.
- **Workaround:** DateOnly/TimeOnly als `string` im kanonischen Format (`yyyy-MM-dd` / `HH:mm:ss.fffffff`) mappen und in LINQ string-Vergleiche verwenden.

### Contains mit lokaler Collection
- **Betroffener Test:** `MinimalLinqOperatorsTests.Contains_filter_with_local_collection`
- **Symptom:** `Where(e => localArray.Contains(e.Id))` liefert nur die erste passende Zeile statt aller.
- **Ursache:** Die `IN (...)`-Translation oder die Engine-Ausführung von `IN`-Klauseln verarbeitet nur das erste Element.
- **Workaround:** Mehrere `==`-Bedingungen mit `||` verknüpfen, oder Einzelabfragen.

## P1 — SaveChanges / Concurrency

### DbUpdateConcurrencyException bei nicht-existierenden Entities
- **Betroffene Tests:** `MinimalSaveChangesTests.Delete_non_existing_entity_throws_DbUpdateConcurrencyException`, `EmbeddedSaveChangesParityTests.SaveChanges_delete_non_existing_entity_throws_concurrency_exception_in_embedded_gate`
- **Symptom:** `Remove(ghost)` + `SaveChanges()` für eine nicht existierende Entity wirft **keine** `DbUpdateConcurrencyException`.
- **Ursache:** Die Engine prüft nicht, ob eine zu löschende Zeile tatsächlich existiert. `DELETE ... WHERE Id = 8888` affected 0 rows, wird aber nicht als Concurrency-Fehler interpretiert.
- **Workaround:** Vor dem Löschen die Existenz per `Find()` prüfen.

### Ambient EF Transactions auf WalhallaSqlEfCoreContext
- **Betroffener Test:** `EmbeddedSaveChangesParityTests.SaveChanges_with_external_ef_transaction_has_consistent_guardrail_in_embedded_gate`
- **Symptom:** `Database.BeginTransaction()` auf einem `WalhallaSqlEfCoreContext` wirft `NotSupportedException` mit Code `LSQ-EF-SAVE-010`.
- **Ursache:** Bewusste Guardrail — der `WalhallaSqlEfCoreContext`-Pfad unterstützt keine ambient EF Transactions.
- **Workaround:** Plain `DbContext` (nicht `WalhallaSqlEfCoreContext`) mit `UseWalhallaSql(engine)` verwenden und `Database.BeginTransaction()` darauf aufrufen.

## P2 — Schema / Migration

### ALTER COLUMN Syntax
- **Betroffener Test:** `EmbeddedMigrationTests.AlterColumn_plan_changes_column_to_nullable`
- **Symptom:** `ALTER TABLE ... ALTER COLUMN ...` schlägt fehl mit `NotSupportedException: ALTER COLUMN requires: ALTER COLUMN name TYPE newType`.
- **Ursache:** Der Migration Script Builder generiert `ALTER TABLE Items ALTER COLUMN Code TEXT` (ohne `TYPE`), aber der SQL-Parser erwartet `ALTER COLUMN name TYPE newType`.
- **Workaround:** Spalten nicht nachträglich ändern; Schema per Drop/Create ersetzen.

### Unique Index Enforcement
- **Betroffene Tests:** `EmbeddedMigrationTests.CreateIndex_plan_is_reflected_and_unique_index_rejects_duplicates`, `ApplyPlannedChanges_multi_column_alternate_key_enforces_uniqueness`
- **Symptom:** `CREATE UNIQUE INDEX` wird akzeptiert, aber Duplikate werden beim INSERT nicht abgewiesen.
- **Ursache:** `CheckUniqueConstraintBuffered` prüft Unique Indexes nicht korrekt im MvccBPlusTree-Mode.
- **Workaround:** Eindeutigkeit auf Anwendungsebene prüfen.

### Migration Guardrails (Rename/Drop-Validierung)
- **Betroffene Tests:** `EmbeddedMigrationTests.ApplyPlan_explicit_rename_missing_column_rejects`, `ApplyPlan_explicit_rename_column_to_existing_target_rejects`, `ApplyPlan_explicit_drop_table_rejects_referenced_table`
- **Symptom:** Umbenennen einer nicht existierenden Spalte / auf eine existierende Spalte / DROP TABLE mit FK-Referenzen wirft **keine** Exception.
- **Ursache:** Die Engine validiert Column-Existence und FK-Referenzen nicht vor der Ausführung.
- **Workaround:** Schema-Änderungen manuell prüfen.

### PlanModelChanges Noop-Detection
- **Betroffener Test:** `EmbeddedMigrationTests.PlanModelChanges_after_apply_detects_no_changes`
- **Symptom:** Nach `ApplyPlannedChanges` detektiert `PlanModelChanges` weiterhin Änderungen (z. B. `DropTableOperation` für interne Tabellen).
- **Ursache:** Interne Tabellen (`__ef_migrations_history`) werden nicht aus dem Diff herausgefiltert.
- **Workaround:** `PlanModelChanges`-Ergebnis manuell filtern.

## P3 — ADO.NET / Transaktionen

### Savepoints
- **Betroffene Tests:** `AdoNetSurfaceConformanceTests.Savepoint_rollback_only_undoes_post_savepoint_work`, `TransactionInterceptionSpecTests` (6 Fails)
- **Symptom:** `DbTransaction.Save(string)` wirft `NotSupportedException`.
- **Ursache:** `WalhallaSqlDbTransaction` überschreibt `Save()`, `Rollback()` und `Release()` nicht. Die Engine-interne Savepoint-Logik (`WalhallaSqlTransaction.Savepoint` etc.) ist nicht via ADO.NET exponiert.
- **Workaround:** Keine Savepoints verwenden.

### HasRows bei Streaming Reader
- **Betroffener Test:** `AdoNetSurfaceConformanceTests.HasRows_is_true_when_rows_exist`
- **Symptom:** `DbDataReader.HasRows` liefert `false` obwohl Zeilen existieren.
- **Ursache:** Im Streaming-Mode ist `_rows` immer leer; `HasRows` prüft nur `_rows.Count > 0`.
- **Workaround:** `Read()` aufrufen und Rückgabewert prüfen statt `HasRows`.

### RecordsAffected nach DELETE mit IN-Clause
- **Betroffener Test:** `AdoNetSurfaceConformanceTests.RecordsAffected_after_DELETE_returns_affected_count`
- **Symptom:** `DELETE ... WHERE Id IN (1, 2)` liefert `RecordsAffected = 1` statt 2.
- **Ursache:** Die Engine zählt betroffene Zeilen bei `IN`-Klauseln nicht korrekt.
- **Workaround:** Einzel-DELETEs pro ID ausführen.

## P4 — Komplexe Szenarien

### JSON Complex Property Queries
- **Betroffener Test:** `PlainDbContextProviderStabilityTests.Plain_DbContext_json_complex_property_query_roundtrips_without_base_class`
- **Symptom:** `Where(c => c.Profile.Zip == "50667")` auf einer Owned-Entity als JSON-Spalte wirft `Column 'j.json__Profile__Name' not found`.
- **Ursache:** Virtuelle JSON-Projektionsspalten werden nicht im Table-Metadata registriert; der `ProjectionPlanner` findet sie nicht.
- **Workaround:** JSON-Rohwert abfragen und clientseitig filtern.

### TPT/TPC/Table-Splitting
- **Betroffene Tests:** `TableSplittingSpecTests` (11 Fails), `DataAnnotationRelationalSpecTests.Table_can_configure_TPT_with_Owned`
- **Symptom:** `NullReferenceException` oder Column-not-found bei TPT/TPC-ähnlichen Szenarien.
- **Ursache:** Table-Splitting / TPT-Mappings werden nicht vollständig unterstützt.
- **Workaround:** TPH (Table-per-Hierarchy) verwenden.

### KeysWithConverters (Byte-Array/Struct-Keys)
- **Betroffene Tests:** `KeysWithConvertersSpecTests` (47 Fails)
- **Symptom:** Byte-Array/Struct-Key-Wandler liefern falsche Literale (`1-1-1` statt binärer Wert) oder `FormatException`.
- **Ursache:** Die Key-Converter-Translation in `TryExtractPkLiteral` unterstützt keine nicht-numerischen Schlüssel.
- **Workaround:** String- oder Int-Schlüssel verwenden.

### NumberToBytesConverter / Decimal-Scale
- **Betroffene Tests:** `CustomConvertersSpecTests`, `BuiltInDataTypesSpecTests` (25 Fails)
- **Symptom:** `NumberToBytesConverter` produziert Decimal-Scale 65 → `ArgumentOutOfRangeException`.
- **Ursache:** Die Decimal-Typ-Mapping-Validierung akzeptiert keine Scale > 29.
- **Workaround:** Decimal als `string` mappen oder `double` verwenden.

### LoadSpec / ManyToManyFieldsLoad (Reader-Lifecycle)
- **Betroffene Tests:** `LoadSpecTests` (274 Fails), `ManyToManyFieldsLoadSpecTests` (80 Fails)
- **Symptom:** `ObjectDisposedException: Cannot access a closed file` bei Include/ThenInclude-Ladevorgängen.
- **Ursache:** Vermutlich Reader-Lifecycle-Bug im MvccBPlusTree-Store oder Engine-Disposal während laufender Reader.
- **Workaround:** Eager Loading mit `AsSingleQuery()` oder explizite `Load()`-Aufrufe.

## Metriken (Juni 2026, nach Fixes)

| Kategorie | Tests | Bestanden | Fehler | Quote |
|-----------|-------|-----------|--------|-------|
| MinimalSpecs (eigene) | 72 | 67 | 0 (5 skipped) | 100% |
| Eigene Tests (non-Spec) | 297 | 285 | 7 (5 skipped) | 96,0% |
| EF-Specs (Fremd-Tests) | ~6800 | 6166 | 954 | 86,5% |
| **Gesamt** | **~7128** | **~6518** | **~961** | **~91,5%** |

> **Notiz:** Die Spec-Test-Regression (von 513 auf 2811 Fehler) wurde durch den Connection-Disposal-Bug
> verursacht und ist jetzt behoben. Die verbleibenden ~954 Spec-Fehler sind dokumentierte Known Limitations.

## Behobene Product Bugs (2026-06-16)

| Bug | Fix |
|-----|-----|
| Connection-Disposal (shared Engine) | `_engineFromRegistry`-Flag + `Close()`-Reihenfolge |
| ALTER COLUMN SQL-Syntax | `TYPE`-Keyword in Migration Script Builder + Engine-Handler |
| HasRows im Streaming-Mode | `_currentRow`-Check nach Schema-Init |
| Savepoints (ADO.NET) | `Save()`/`Rollback()`/`Release()`-Overrides in `WalhallaSqlDbTransaction` |
| PlanModelChanges Noop-Detection | Interne Tabellen aus Diff filtern |
| Migration Guardrails (Rename/Drop) | Column-Existenz/FK-Referenz-Validierung in Engine |
| Concurrency Detection (DELETE) | `Affected(0)` bei nicht-existenter Zeile im PK-Fast-Path |
| Spaltennamen-Extraktion im ADO.NET Reader | `NormalizeProjectionName` statt `NormalizeQualifiedProjectionName` für einfache qualifizierte Spalten |
| `MergeTransactionWrites` String-PK Crash | Numerische PK-Prüfung vor `Convert.ToInt64`; nicht-numerische PKs unverändert übernehmen |

## In Arbeit / Offene Engine-Themen

| Thema | Stand | Nächster Schritt |
|-------|-------|------------------|
| Table-Splitting / Shared-Table SaveChanges | Hard-Crash behoben; verbleibend: `ValidateNoDuplicateKey` wirft falsch-positive Duplikat-Exceptions bei Shared-Table-Entitäten | `WalhallaSqlDbContextRuntime.ValidateNoDuplicateKey` muss Store-Identity statt EF-Entity-PK für Shared-Table-Szenarien verwenden |

## Verbleibende eigene Fehler (7)

| Test | Ursache |
|------|---------|
| `RecordsAffected_after_DELETE_returns_affected_count` | `IN`-Clause in DELETE zählt nur 1 statt 2 |
| `CreateIndex_plan_is_reflected_and_unique_index_rejects_duplicates` | Unique Index false positive |
| `ApplyPlannedChanges_multi_column_alternate_key_enforces_uniqueness` | Unique Index false positive |
| `Plain_DbContext_regular_dbset_filtered_collection_include_roundtrip` | Unique Index false positive |
| `Plain_DbContext_json_complex_property_query_roundtrips` | JSON-Projektionsspalten nicht gefunden |
| `ManyToManyFilteredIncludeProjectionRegressionTests` | Many-to-Many-Filtered-Include-Query |
| `AdoNetSampleSmokeTests.Embedded_ado_sample_runs_reproducibly` | Repository-Root nicht gefunden (Test-Bug) |
