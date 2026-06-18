# Phase C.7 — Statistiken & kostenbasierter Planner (Detailplanung)

**Stand der Erstellung:** 2026-06-02
**Status:** ✅ abgeschlossen — C.7.1 ✅ C.7.2 ✅ C.7.3 ✅ C.7.4 ✅ C.7.5 ✅ C.7.6 ✅ C.7.7 ✅ C.7.8 ✅ C.7.9 ✅
**Voraussetzung:** C.0–C.5 ✅ (436/436 Tests grün, net8.0/9.0/10.0)
**Tests (Stand 2026-06-02):** 485 grün (481 WalhallaSql + 4 PgWire) auf net8.0/9.0/10.0
**Blockiert:** Phase D (Embedded v1.0 Release — API-Freeze + SQLite-Vergleich) profitiert direkt; B.x referenziert „ANALYZE aus Phase C.7" als Cardinality-Quelle.

---

## 1. Ziel & Motivation

Heute schätzt der Planner Kardinalitäten ausschließlich aus **rohen Zeilenzahlen**
(`TableStore.CountRows`) und **statischen Heuristiken**:

- `IndexSelector.ScoreIndex` vergibt feste Punkte (`equalityCount*10 + rangeCount*5 + unique?20 + covering?30`) — **ohne** Wissen über die tatsächliche Selektivität eines Prädikats.
- `JoinStrategyEstimator.Estimate` / `JoinStrategySelector.Select` entscheiden Hash- vs. Sort-Merge-Join allein über `leftCount`/`rightCount` (= Gesamtzeilen), **nicht** über die geschätzte Ergebnisgröße nach Filterung.
- `EXPLAIN` gibt `~{CountRows}` als Tabellengröße aus — **kein** geschätztes Zeilenergebnis pro Plan-Knoten.

Das verfehlt zwei Dinge:
1. Ein Prädikat `WHERE status = 'rare'` (1 % Trefferquote) und `WHERE status = 'common'` (90 %) werden identisch behandelt → falsche Index-/Join-Wahl.
2. Das v1-Performance-Ziel **„SQLite ±2× (Embedded Mixed-Workload)"** ist ohne selektivitätsbasierte Planwahl bei nicht-trivialen Queries schwer haltbar.

**C.7 liefert:** `ANALYZE`, das pro Spalte Histogramme + Häufigkeitsstatistiken aufbaut, einen `SelectivityEstimator`, der diese nutzt, und die Verdrahtung in `IndexSelector` + `JoinStrategyEstimator` + `EXPLAIN`.

---

## 2. Exit-Kriterien (aus phase-C, Zeile 14)

- [x] `ANALYZE` aktualisiert Histogramme; Planner nutzt sie **messbar** (EXPLAIN-Output-Differenz vorher/nachher). *(C.7.2 + C.7.3 + C.7.4 + C.7.6 ✅)*
- [x] `ANALYZE [table]` und `ANALYZE` (alle Tabellen) parsen + ausführen, mirror zu `VACUUM`. *(C.7.2 ✅)*
- [x] EXPLAIN zeigt geschätzte Zeilen pro Knoten (`rows=~N`); Schätzung ändert sich nach `ANALYZE` nachweisbar. *(C.7.6 ✅)*
- [x] Index-Wahl bei zwei kandidierenden Indizes wählt den selektiveren (Differential-Test). *(C.7.4 ✅)*
- [x] Join-Strategie nutzt geschätzte Ergebnisgröße statt roher Tabellengröße. *(C.7.5 ✅)*
- [x] Statistiken überleben Engine-Restart (Persistenz im Katalog, backward-compatible). *(C.7.7 ✅)*
- [x] `pg_stats`-Virtual-Table über PgWire (mirror zu `pg_collation` aus C.4). *(C.7.8 ✅)*
- [x] Telemetrie: Trace-Token + Counter für `ANALYZE`-Lauf und Estimator-Treffer (`WalhallaSql` Meter + ActivitySource). *(C.7.9 ✅)*
- [x] BenchmarkDotNet-Vorher/Nachher auf Mixed-Workload, Snapshot in `docs/perf/statistics.md`. *(C.7.9 ✅)*
- [x] Keine Regression: bestehende Suite grün auf net8.0/9.0/10.0. *(485/485 ✅)*

---

## 3. Statistik-Modell

Angelehnt an Postgres `pg_statistic` / `pg_stats`, bewusst auf v1-Minimalumfang reduziert:

### 3.1 Pro Tabelle (`TableStatistics`)
| Feld | Typ | Quelle |
|---|---|---|
| `RowCount` | `long` | exakter `CountRows` zur ANALYZE-Zeit |
| `AnalyzedAt` | `DateTime` (UTC) | Zeitstempel |
| `Columns` | `ColumnStatistics[]` | pro Spalte |

### 3.2 Pro Spalte (`ColumnStatistics`)
| Feld | Typ | Bedeutung | Postgres-Pendant |
|---|---|---|---|
| `NullFraction` | `double` | Anteil NULL-Werte | `null_frac` |
| `DistinctCount` | `long` | Anzahl distinkter Nicht-NULL-Werte | `n_distinct` |
| `AverageWidth` | `int` | Ø Bytebreite (für I/O-Kosten, optional v1) | `avg_width` |
| `MostCommonValues` | `(object Value, double Frequency)[]` | Top-N MCV (default N=32) | `most_common_vals`/`most_common_freqs` |
| `Histogram` | `object[]` | Equi-Depth-Grenzen (default 64 Buckets) der Nicht-MCV-Werte | `histogram_bounds` |

**Sampling:** v1 = **Full-Scan** (deterministisch, einfach, korrekt). Großtabellen-Reservoir-Sampling
(`SELECT ... TABLESAMPLE`) ist v1.x-Backlog — Hook im Design vorsehen (`AnalyzeOptions.SampleSize?`).

---

## 4. Selektivitäts-Formeln (`SelectivityEstimator`)

Eingabe: `SargablePredicate` + `ColumnStatistics`. Ausgabe: Selektivität ∈ [0,1].
Fallback ohne Statistik = heutige Default-Heuristik (siehe §4.5).

| Prädikat | Formel mit Statistik |
|---|---|
| `col = v` | wenn `v ∈ MCV` → `MCV.Frequency`; sonst `(1 - Σ MCV.Freq - NullFraction) / max(1, DistinctCount - MCV.Count)` |
| `col <> v` | `1 - sel(col = v) - NullFraction` |
| `col < v` / `<=` / `>` / `>=` | Histogramm-Bucket-Interpolation (Anteil der Buckets unter/über `v`, linear innerhalb des Grenz-Buckets) |
| `col BETWEEN a AND b` | `sel(col >= a) - sel(col > b)` über Histogramm |
| `col IS NULL` | `NullFraction` |
| `col IS NOT NULL` | `1 - NullFraction` |
| `col IN (v1..vk)` | `Σ sel(col = vi)`, geklemmt auf [0,1] |
| `col LIKE 'prefix%'` | Histogramm-Range über `[prefix, prefix+\uffff]`; sonst Default 0.25 |
| AND | Produkt der Selektivitäten (Unabhängigkeitsannahme) |
| OR | `1 - Π(1 - sel_i)` |

### 4.5 Defaults ohne Statistik (heutiges Verhalten bewahren)
- `=` → `1 / max(1, DistinctCountGuess)` mit `DistinctCountGuess = sqrt(RowCount)` falls keine Stats; sonst Postgres-Standard `0.005`.
- Range → `0.3`; `<>` → `0.7`; `LIKE` → `0.25`; unbekannt → `0.5`.

**Geschätzte Ergebniszeilen** eines Scans = `RowCount * Π sel(Prädikate)`, min. 1.

---

## 5. Sub-Slices

> Jeder Sub-Slice ist eigenständig grün (Build + Tests + Bench-Delta), mergebar.

### C.7.1 — Statistik-Datenmodell + In-Memory-Katalog
**Inhalt**
- `WalhallaSql/Statistics/TableStatistics.cs`, `ColumnStatistics.cs` (immutable records).
- `StatisticsCatalog` — `ConcurrentDictionary<int, TableStatistics>` (key = tableId), thread-safe Lookup/Replace, unter dem **Catalog-Lock** (C.0 E.1 — `_catalogLockManager`).
- API: `TryGet(int tableId, out TableStatistics)`, `Set(int tableId, TableStatistics)`, `Invalidate(int tableId)`.

**Exit:** Unit-Tests für Set/Get/Invalidate; kein Planner-Konsum noch.
**Files (neu):** `WalhallaSql/Statistics/{TableStatistics,ColumnStatistics,StatisticsCatalog}.cs`

---

### C.7.2 — `ANALYZE`-Statement (Parsing + Ausführung)
**Inhalt**
- `SqlAnalyzeStatement(string? TableName)` in `SqlStatement.cs` (mirror `SqlVacuumStatement`, Zeile 230).
- `ParseAnalyze` in `SqlStatementParser.cs` (mirror `ParseVacuum`, Zeile 2651) — `ANALYZE` / `ANALYZE <table>`.
- `StatisticsBuilder` — Full-Scan via `TableStore.ScanWithPredicate` (predicate=null), berechnet pro Spalte:
  - `RowCount`, `NullFraction`, `DistinctCount` (HashSet bzw. HyperLogLog-optional),
  - MCV (Top-32 per Häufigkeit), Histogramm (Equi-Depth, 64 Buckets, sortierte Restwerte).
- `WalhallaEngine.Analyze(string? table)` + `ExecuteAnalyze` in **beiden** Execute-Pfaden (mirror `ExecuteVacuum`, Zeile 651 + 631).
- Dispatch-`case SqlAnalyzeStatement` in beiden `switch`-Blöcken (Zeile ~255 + ~631).
- Rückgabe: `WalhallaResultSet.Affected(analyzedTableCount)`.

**Exit:** `ANALYZE`/`ANALYZE t` füllen den `StatisticsCatalog`; Tests prüfen RowCount/NullFraction/DistinctCount/MCV/Histogramm-Inhalt gegen bekannte Datensätze.
**Files:** `WalhallaSql/Sql/SqlStatement.cs`, `WalhallaSql/Parsing/SqlStatementParser.cs`, `WalhallaSql/Statistics/StatisticsBuilder.cs` (neu), `WalhallaSql/Api/WalhallaEngine.cs`, `WalhallaSql/PublicAPI.Unshipped.txt`

---

### C.7.3 — `SelectivityEstimator` (reine Funktion, voll testbar)
**Inhalt**
- `WalhallaSql/Statistics/SelectivityEstimator.cs` — statische Methoden gemäß §4.
- `EstimateSelectivity(SqlWhereExpression?, ColumnStatistics?-Lookup) → double`.
- `EstimateRows(long rowCount, SqlWhereExpression?, stats) → long`.
- Histogramm-Interpolation + MCV-Lookup; Fallback-Defaults bei fehlender Statistik.

**Exit:** Umfangreiche Unit-Tests (jede Formel aus §4, AND/OR-Komposition, Fallback-Pfad). **Kein** Engine-State nötig — pure Funktion.
**Files (neu):** `WalhallaSql/Statistics/SelectivityEstimator.cs`

---

### C.7.4 — Planner-Integration: kostenbasierte Index-Wahl
**Inhalt**
- `IndexSelector.ScoreIndex` (Zeile 285) erhält optional `ColumnStatistics`-Lookup.
- Neuer Tie-Breaker: bei gleichem struktur-Score entscheidet die **geschätzte Selektivität** der gematchten Prädikate (niedriger = besser). GIN-Sonderfall (Score 200) bleibt unangetastet.
- `QueryPlanner.BuildSelect` (Zeile 81) reicht `StatisticsCatalog.TryGet(tableId)` an `SelectBestIndex` durch.
- Score bleibt rückwärtskompatibel, wenn keine Statistik vorhanden (heutiges Verhalten).

**Exit:** Differential-Test: zwei Indizes auf derselben Tabelle, einer selektiver; nach `ANALYZE` wählt der Planner den selektiveren. Vorher (ohne Stats) = altes Verhalten.
**Files:** `WalhallaSql/Execution/IndexSelector.cs`, `WalhallaSql/Execution/QueryPlanner.cs`

---

### C.7.5 — Planner-Integration: kardinalitätsbasierte Join-Strategie
**Inhalt**
- `JoinStrategyEstimator.Estimate` (Zeile 142) bekommt **geschätzte** Left/Right-Größen (nach lokalen WHERE-Filtern), nicht roh `CountRows`.
- `QueryPlanner.BuildJoinPlan` bzw. `JoinStepExecutor` berechnet pro Join-Eingang `EstimateRows` aus dem `StatisticsCatalog`.
- `EXPLAIN`-Join-Pfad (Zeile 685–715) nutzt dieselben Schätzungen → die `~{estLeft}x{rightCount}`-Details werden statistikgespeist.

**Exit:** Differential-Test: stark gefilterter linker Eingang kippt die Strategie von Hash auf Sort-Merge/NestedLoop messbar (Trace-Label-Diff vor/nach `ANALYZE`).
**Files:** `WalhallaSql/Execution/Join/JoinStepExecutor.cs`, `WalhallaSql/Execution/QueryPlanner.cs`

---

### C.7.6 — EXPLAIN mit Zeilen-Schätzung (Exit-Kriterium-Beweis)
**Inhalt**
- `ExecuteExplain` (Zeile 666) ergänzt pro Knoten `rows=~N` aus `SelectivityEstimator.EstimateRows`.
- Neue Detail-Spalte oder Suffix in `Details`: `... (est_rows=~N)`.
- Output ist **vor** `ANALYZE` (Default-Heuristik) und **nach** `ANALYZE` (echte Stats) **unterschiedlich** → erfüllt das harte Exit-Kriterium „EXPLAIN-Output-Differenz".

**Exit:** Test `Explain_EstimatedRows_ChangesAfterAnalyze` — gleiche Query, EXPLAIN-Zeilenschätzung differiert vor/nach `ANALYZE`.
**Files:** `WalhallaSql/Api/WalhallaEngine.cs`

---

### C.7.7 — Persistenz der Statistiken (Restart-fest)
**Inhalt**
- Statistiken in den **Katalog** serialisieren (mirror Collation-Persistenz aus C.4 in `TableStore.cs`): binär, backward-compatible, eigener Record-Typ/Prefix.
- Beim Engine-Open: Statistiken in `StatisticsCatalog` rehydrieren.
- `ANALYZE` schreibt unter `TableWriteLock(0)` + WAL-Append (wie VACUUM), damit ein Crash mitten in ANALYZE atomar ist (alt-Statistik bleibt gültig oder neu vollständig).
- Bei `DROP TABLE` / `ALTER TABLE` (Spalten-Änderung): zugehörige Statistik invalidieren.

**Exit:** Test: `ANALYZE` → Engine-Restart → Statistik + Plan-Verhalten erhalten. Backward-Compat-Test: alter Katalog **ohne** Statistik-Record lädt fehlerfrei.
**Files:** `WalhallaSql/Storage/TableStore.cs`, `WalhallaSql/Api/WalhallaEngine.cs`

---

### C.7.8 — PgWire `pg_stats`-Virtual-Table
**Inhalt**
- `pg_stats`-View (mirror `pg_collation` aus C.4): Spalten `schemaname, tablename, attname, null_frac, n_distinct, most_common_vals, most_common_freqs, histogram_bounds`.
- Aus `StatisticsCatalog` projiziert; leer, wenn nie `ANALYZE` lief.

**Exit:** PgWire-Integrationstest: `SELECT * FROM pg_stats WHERE tablename='...'` liefert die Statistik nach `ANALYZE`.
**Files:** `WalhallaSql.PgWire/` (entsprechende Virtual-Table-Registry), Test in `WalhallaSql.PgWire.Tests/`

---

### C.7.9 — Telemetrie + Bench + Doku (Querschnitt, Abschluss)
**Inhalt**
- Trace-Token + Counter (Pattern `WalhallaSql.Diagnostics.*`): `analyze.tables`, `analyze.duration_ms`, `estimator.hits` (Statistik genutzt) vs. `estimator.fallbacks`.
- BenchmarkDotNet in `BenchmarkSuite1/`: Mixed-Workload (selektive + unselektive Prädikate, 2-Tabellen-Join) **vor/nach** `ANALYZE`.
- `docs/perf/statistics.md` — Vorher/Nachher-Snapshot + GIN-Style-Speedup-Tabelle.
- `docs/` — Feature-Seite „ANALYZE & Planner-Statistiken".

**Exit:** Bench-Delta dokumentiert; Counter sichtbar; Doku committet.
**Files:** `WalhallaSql/Diagnostics/*`, `BenchmarkSuite1/*`, `docs/perf/statistics.md`, `docs/`

---

## 6. Abhängigkeitsgraph

```
C.7.1 (Datenmodell + Katalog)
   ├─ C.7.2 (ANALYZE: Parse + Build) ──────────────┐
   ├─ C.7.3 (SelectivityEstimator, pure) ──────────┤
   │                                               ▼
   │        C.7.4 (Index-Wahl)  ◄── braucht 7.2 + 7.3
   │        C.7.5 (Join-Strategie) ◄── braucht 7.2 + 7.3
   │        C.7.6 (EXPLAIN est_rows) ◄── braucht 7.3  [BEWEIST Exit-Kriterium]
   │
   ├─ C.7.7 (Persistenz) ◄── braucht 7.2
   ├─ C.7.8 (pg_stats) ◄── braucht 7.1/7.2
   └─ C.7.9 (Telemetrie + Bench + Doku) ◄── Abschluss, braucht 7.4–7.6
```

**Empfohlene Reihenfolge:** 7.1 → 7.3 (parallel zu 7.2) → 7.2 → 7.6 (früher Exit-Beweis) → 7.4 → 7.5 → 7.7 → 7.8 → 7.9.

---

## 7. Verifikation (phasenübergreifend)

- **Differential-Tests** gegen Postgres-Verhalten: gleiche Query-/Daten-Form, EXPLAIN-Plan-Wahl qualitativ vergleichbar (selektiverer Index/passendere Join-Strategie).
- **Estimator-Genauigkeit:** geschätzte vs. tatsächliche Zeilen auf synthetischen Verteilungen (uniform, skewed/Zipf, viele NULLs) — Fehlerfaktor dokumentiert (Ziel: innerhalb 4× für MCV/Histogramm-abgedeckte Prädikate).
- **Regression:** volle `WalhallaSql.Tests`-Suite grün (net8.0/9.0/10.0).
- **Crash:** ein `WalhallaSql.CrashTests`-Szenario „Crash während ANALYZE" → alte Statistik intakt oder neue vollständig (atomar).

---

## 8. Risiken & Mitigation

| Risiko | Mitigation |
|---|---|
| **Falsche Schätzung → schlechterer Plan als Heuristik** | Statistik nur als **Tie-Breaker** bei Index-Wahl; Default-Heuristik bleibt Fallback. Kein Plan-Regress, wenn Stats fehlen. |
| **ANALYZE-Kosten (Full-Scan) auf Großtabellen** | v1 dokumentiert als O(n)-Operation; Reservoir-Sampling-Hook (`AnalyzeOptions.SampleSize`) im Design vorgesehen, Impl. v1.x. |
| **Unabhängigkeitsannahme (AND = Produkt) unterschätzt korrelierte Spalten** | Akzeptiert für v1 (wie Postgres ohne `CREATE STATISTICS`); dokumentiert. Multivariate Stats = v1.x. |
| **Persistenz-Format-Bruch** | Eigener Katalog-Record-Typ mit Längen-Präfix, backward-compatible (alt = kein Record). Explizit getestet. |
| **MVCC-Interaktion:** Stats werden zwischen Snapshots „stale" | Akzeptiert (wie Postgres): Statistik ist eine Momentaufnahme; Estimator-Fehler ist tolerierbar, da nur Plan-Heuristik. Kein Korrektheits-Impact. |

---

## 9. Geschätzter Umfang

**9 Sub-Slices** (C.7.1–C.7.9). Nicht-Ziele für v1 (→ v1.x-Backlog):
Reservoir-Sampling · multivariate/erweiterte Statistiken (`CREATE STATISTICS`) ·
HyperLogLog-Distinct · Auto-ANALYZE-Daemon · Kosten-Modell mit I/O-/CPU-Gewichten
(v1 bleibt rein kardinalitätsbasiert).

## 10. Definition of Done für C.7 (Phase-C-Abschluss)

- [x] Alle 9 Sub-Slices grün, Suite grün auf net8.0/9.0/10.0. *(C.7.9 Bench/Doku noch offen)*
- [x] EXPLAIN-Output-Differenz vor/nach `ANALYZE` per Test bewiesen. *(C.7.6 ✅)*
- [x] Persistenz + Backward-Compat getestet. *(C.7.7 ✅)*
- [x] `pg_stats` über PgWire abfragbar. *(C.7.8 ✅)*
- [x] Bench-Snapshot + Doku committet. *(C.7.9 ✅)*
- [x] `phase-C-storage-mvcc.md` Status-Block + Graph auf „C.7 ✅" aktualisiert; `master-plan.md` nachgezogen. *(C.7.9 ✅)*
- [x] **Phase C für v1 vollständig** → **Übergang zu Phase D**.
