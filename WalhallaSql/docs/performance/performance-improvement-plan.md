# Performance-Verbesserungsplan: LayeredSql Embedded

Stand: 12.05.2026

## Ausgangslage (Benchmark-Zahlen vom 09.05.2026)

| Benchmark    | LayeredSql | MSSQL    | SQLite   | Alloc LayeredSql | Alloc SQLite |
|--------------|------------|----------|----------|------------------|--------------|
| SelectById   | 65 μs      | 80 μs    | 43 μs    | **59 KB**        | 1 KB         |
| SelectRange  | 658 μs     | 645 μs   | 262 μs   | **788 KB**       | 1.2 KB       |
| SelectJoin   | **5358 μs**| 1162 μs  | 463 μs   | **6432 KB**      | 1.3 KB       |
| InsertBatch  | —          | —        | —        | hoch             | —            |

Benchmark-Umgebung: AMD Ryzen 9 3900X · .NET 8.0.26 · BenchmarkDotNet ShortRun (WarmupCount=1, IterationCount=3)

## Messergebnisse nach Phasen 1a–5b (10.05.2026)

Benchmark-Lauf nach Implementierung aller Phasen 1a bis 5b. Commit-Branch: `feature/StoredProcedures`.

### LayeredSql InMemory

| Benchmark    | Latenz (Ist) | Latenz (Baseline) | Δ Latenz | Alloc (Ist)   | Alloc (Baseline) | Δ Alloc | Ziel Alloc   | Status |
|--------------|-------------:|------------------:|---------:|--------------:|-----------------:|--------:|:-------------|:-------|
| SelectById   | 47.58 μs     | 65.87 μs          | -28%     | 47.13 KB      | 59.78 KB         | -21%    | < 5 KB       | ❌ |
| SelectRange  | 651.10 μs    | 658.54 μs         | -1.1%    | 775.25 KB     | 787.88 KB        | -1.6%   | < 50 KB      | ❌ |
| SelectJoin   | 5 134 μs     | 5 357.91 μs       | -4.2%    | 6 423.92 KB   | 6 432.36 KB      | -0.1%   | < 1500 μs / < 500 KB | ❌ |
| InsertBatch  | 25 072 ms    | —                 | —        | 6 900 000 KB  | —                | —       | —            | — |
| UpdateSingle | 158.17 ms    | —                 | —        | 56 701 KB     | —                | —       | —            | — |
| DeleteSingle | 338.51 ms    | —                 | —        | 113 366 KB    | —                | —       | —            | — |

### SQLite InMemory (Referenz, gleicher Lauf)

| Benchmark    | Latenz   | Alloc    |
|--------------|----------:|----------:|
| SelectById   | 5.12 μs   | 1.00 KB   |
| SelectRange  | 179.13 μs | 1.20 KB   |
| SelectJoin   | 417.79 μs | 1.29 KB   |
| InsertBatch  | 506.98 μs | 149.12 KB |
| UpdateSingle | 5.36 μs   | 1.06 KB   |
| DeleteSingle | 11.73 μs  | 2.02 KB   |

### Bewertung

Die Latenz von **SelectById verbesserte sich um 28%** (65.87 → 47.58 μs) — sichtbarer Gewinn durch RowBuffer + Covering-Index-Pfad + O(n)-Eliminierung. Die **Allokationsziele wurden nicht erreicht**: Die Dictionary-pro-Zeile-Struktur ist nach wie vor die dominante Quelle (GC.Gen0 = 5.7 pro 1000 Ops bei SelectById). Phase 2a hat zwar den Projektion-Pfad umgestellt, aber die Kernallokation entsteht tiefer im Stack (Row-Materialisierung aus dem Storage-Layer), wo `object?[]`/`RowBuffer` noch nicht greift.

Für SelectRange und SelectJoin ist die Verbesserung marginal (<2%), weil der Engpass nicht im Projektion-Pfad, sondern in der Index-Scan-/Join-Verarbeitung liegt.

**Testregression-Status:** Beide als Regression identifizierten Failures (`OverflowException GetFieldValue<T>` und `Correlated_in_subquery_with_residual_filter`) sind **pre-existing auf dem Parent-Commit** `4582259` — nicht durch diese Phasen eingeführt.

**Kern-Befund:** Die Zeit bei SelectById und SelectRange ist akzeptabel. Das eigentliche Problem ist die **Allokation** (60x–5000x gegenüber SQLite). Unter Last (EF Core, parallele Zugriffe) führt das zu GC-Druck und Durchsatzeinbrüchen. SelectJoin hat zusätzlich ein Zeitproblem (11,5x langsamer als SQLite).

**Root Causes:**
1. Jede Ergebniszeile ist ein `Dictionary<string, object?>` (~200 Bytes Overhead + Boxing jedes primitiven Werts)
2. `GetValue(ordinal)` in `LayeredSqlDbDataReader` geht über Dictionary-Lookup statt direkten Array-Index
3. `TryGetValueFromRow` in `SqlStatementExecutor` hat O(n)-Fallbacks via `FirstOrDefault` + OrdinalIgnoreCase
4. Kein EF Core BenchmarkDotNet-Profil vorhanden — Join-/Union-Muster unmessbar
5. SelectJoin-Zeitlücke zu SQLite ist auch nach April-Arbeit noch offen

---

## Messergebnisse nach Todo-Runde (12.05.2026)

Benchmark-Lauf nach Implementierung von 3 weiteren Optimierungen (Todo 1–3), aufbauend auf den Phasen 1a–5b.

**Implementierte Todos:**
1. `DecodeColumnsToRowBuffer` + `BinaryProjectionPlan` + Fast-Path im Hauptloop (`SqlStatementExecutor`, `RowBinaryCodec`)
2. Object-Pooling für Join-Buckets (`s_joinBucketPool`, `s_joinLookupDictPool`)
3. Type-pattern-matching in allen typed getters (`LayeredSqlDbDataReader`: `GetInt32`, `GetInt64`, `GetString`, `GetDouble`, etc.)

Benchmark-Umgebung: AMD Ryzen 9 3900X · .NET 8.0.26 · BenchmarkDotNet ShortRun (WarmupCount=1, IterationCount=3)

### LayeredSql InMemory

| Benchmark    | Latenz (Ist) | Latenz (Vorher) | Δ Latenz  | Alloc (Ist)  | Alloc (Vorher) | Δ Alloc  | Status |
|--------------|-------------:|----------------:|----------:|-------------:|---------------:|---------:|:-------|
| SelectById   | 48.19 μs     | 47.58 μs        | +1.3% (Rauschen) | 46.79 KB | 47.13 KB    | -0.7%    | — |
| SelectRange  | 515.88 μs    | 651.10 μs       | **-20.8%** | 540.44 KB   | 775.25 KB      | **-30.3%** | ✅ |
| SelectJoin   | 5 475 μs     | 5 134 μs        | +6.6% (±32% Margin) | 6 280.05 KB | 6 423.92 KB | -2.2% | — |

### SQLite InMemory (Referenz, gleicher Lauf)

| Benchmark    | Latenz     | Alloc   |
|--------------|----------:|--------:|
| SelectById   | 5.067 μs  | 1.00 KB |
| SelectRange  | 181.48 μs | 1.20 KB |
| SelectJoin   | 409.41 μs | 1.29 KB |

### Bewertung

**SelectRange** profitiert messbar vom Binary-Projection-Fast-Path (Todo 1): -21% Latenz, -30% Allokation — konsistentes Ergebnis (StdErr 0.12%).

**SelectById** bleibt innerhalb des Messrauschens unverändert — der Fast-Path greift hier nicht, weil Single-Row-Lookup nicht durch den Range-Decode-Pfad läuft.

**SelectJoin**: Nominell +6.6%, aber Margin ±32% (ShortRun, nur 3 Iterationen) — statistisch nicht aussagekräftig. Das Join-Pooling (Todo 2) reduziert Allokation um -2.2%, der Latenz-Unterschied zu SQLite bleibt strukturell (13x).

**Allokationsziele weiterhin verfehlt.** Die dominante Allokationsquelle (Dictionary-Materialisierung aus dem Storage-Layer, ~45 KB/Zeile bei SelectById) ist durch Todos 1–3 nicht adressiert worden — sie liegt unterhalb des Projektion-Pfads.

---

## TL;DR

Schrittweiser Plan in 5 Phasen. Größter ROI in **Phase 2** (Row-Representation-Überarbeitung): Die Dictionary-pro-Zeile-Allokation erklärt den 60x–5000x Allokationsunterschied zu SQLite und erzeugt unter Last GC-Druck. Danach: EF Core Benchmark, O(n)-Fallbacks, Cache-Lücken, verbleibende Join-Zeit.

---

## Phase 1 — Meßbasis erweitern ✅ Implementiert

### 1a. In-Memory-Benchmarks ✅

Zwei neue Klassen in `LayeredSql.Benchmarks/Program.cs` anhängen.

**Vorbereitung:** `using LayeredSql;` nach `using LayeredSql.AdoNet;` einfügen (für `EngineProvider`, `EphemeralEngine`).

```csharp
// SQLite :memory: — kein Disk-I/O, reiner SQL-Executor-Vergleich
[ShortRunJob, WarmupCount(1), IterationCount(3), MemoryDiagnoser]
[EventPipeProfiler(BenchmarkDotNet.Diagnosers.EventPipeProfile.CpuSampling)]
public class SqliteInMemoryBenchmark : EngineBenchmarkBase
{
    protected override string EngineName => "SQLite";
    protected override DbConnection CreateConnection()
        => new SqliteConnection("Data Source=:memory:");
    [GlobalCleanup] public void SqliteInMemoryCleanup() => Cleanup();
}

// LayeredSql InMemory — MemTableMode.InMemory + WalSyncMode.None, kein ODS-Flush
[ShortRunJob, WarmupCount(1), IterationCount(3), MemoryDiagnoser]
[EventPipeProfiler(BenchmarkDotNet.Diagnosers.EventPipeProfile.CpuSampling)]
public class LayeredSqlInMemoryBenchmark : EngineBenchmarkBase
{
    private EphemeralEngine? _engine;
    protected override string EngineName => "LayeredSql";
    protected override DbConnection CreateConnection()
    {
        _engine = EngineProvider.InMemory();
        var database = _engine.GetOrCreateDatabase("BenchmarkDb");
        return new LayeredSqlDbConnection(database);
    }
    [GlobalCleanup]
    public void LayeredInMemoryCleanup() { Cleanup(); _engine?.Dispose(); }
}
```

Relevante Dateien:
- `LayeredSql.Benchmarks/Program.cs` — Einfügepunkte: Zeile 10 (using) und Ende der Datei (neue Klassen)
- `LayeredSql/EngineProvider.cs` — `InMemory()` ab Zeile ~80 (gibt `EphemeralEngine` zurück)
- `LayeredSql.AdoNet/LayeredSqlDbConnection.cs` — `LayeredSqlDbConnection(IDatabase)` Konstruktor (Zeile ~46)
- `.csproj` — **keine Änderung nötig**, beide Projekte bereits referenziert

Verifikation:
```
dotnet build LayeredSql.Benchmarks -c Release
dotnet run -c Release --project LayeredSql.Benchmarks -- --list flat   # beide Klassen sichtbar
dotnet run -c Release --project LayeredSql.Benchmarks -- -f "*InMemory*"
```

Erwartung: `SqliteInMemoryBenchmark` schlägt `SqliteBenchmark` (File); `LayeredSqlInMemoryBenchmark` schlägt `LayeredSqlBenchmark` (File) — und zeigt den echten Executor-Gap zu SQLite `:memory:`.

### 1b. EF Core Benchmark-Klasse ⏳ Offen

Neue BenchmarkDotNet-Klasse in `BenchmarkSuite1/` oder eigenem Projekt, die den EF Core-Stack (DbContext → `LayeredSqlRelationalConnection` → `LayeredSqlDbDataReader`) mit realistischen Mustern abdeckt:

- Einfaches SELECT mit WHERE (Primärschlüssel)
- JOIN über Navigationsproperty (`.Include(...)`)
- UNION ALL (Table-per-Hierarchy / Table-per-Concrete-Type)
- Parameterisierte Abfragen (EF Core-Muster mit `@p0`, `@p1`)

---

## Phase 2 — Row-Representation-Überarbeitung (höchster ROI) ✅ Teilweise implementiert

> RowBuffer + ColumnSchema eingeführt (Phase 2a) und Ordinal-Fast-Path in DataReader (Phase 2b). Die Dictionary-Allokation im Storage-Layer (unterhalb des Projektion-Pfads) ist noch nicht adressiert — erklärt warum Allokationsziele nicht erreicht wurden.

### 2a. `RowBuffer`-Typ statt Dictionary pro Zeile ✅

**Problem:** Jede Zeile ist `Dictionary<string, object?>`. Bei 1001 Zeilen = 1001 Dictionaries + Boxing aller primitiven Werte = ~788 KB für SelectRange.

**Lösung:** Neuer Typ `RowBuffer` mit `object?[]` (indexiert nach Ordinalposition) und shared `ColumnSchema` (einmal pro ResultSet allokiert):

```csharp
// Einmal pro ResultSet
public sealed class ColumnSchema
{
    public string[] Names { get; }           // lowercase normalisiert
    public Dictionary<string, int> Ordinals { get; }  // Name → Ordinal
    public ColumnSchema(string[] names) { ... }
}

// Pro Zeile — kein Dictionary-Overhead
public readonly struct RowBuffer
{
    public readonly object?[] Values;
    public readonly ColumnSchema Schema;
    public object? GetValue(int ordinal) => Values[ordinal];
    public object? GetValue(string name) => Values[Schema.Ordinals[name]];
}
```

Betroffene Stellen:
- `SqlStatementExecutor.cs` ~Zeile 3420: Projektion baut aktuell `Dictionary<string, object?>` pro Zeile
- `LayeredSqlDbDataReader.cs`: `_rows` ist `List<IReadOnlyDictionary<string, object?>>` → wird `List<RowBuffer>`

### 2b. Ordinal-Fast-Path in `LayeredSqlDbDataReader` ✅

EF Core greift ausschließlich über `GetValue(ordinal)` zu — die Dictionary-Ebene ist hier pure Overhead.

Nach der Row-Representation-Änderung:
```csharp
public override object GetValue(int ordinal)
    => _currentRow.GetValue(ordinal) ?? DBNull.Value;  // direkter Array-Zugriff
```

### 2c. Boxing primitiver Typen reduzieren ⏳ Offen

`GetInt32`, `GetInt64`, `GetString`, `GetDouble` etc. können nach der Umstellung auf `RowBuffer` direkt auf den Array-Slot zugreifen, ohne `(int)(object)value`-Umweg. Ggf. `object?[]` durch typed storage ergänzen (spätere Optimierung).

---

## Phase 3 — Executor-Hotpath-Korrekturen ✅ Implementiert

### 3a. O(n)-Fallbacks in `TryGetValueFromRow` eliminieren ✅

**Aktueller Code** (SqlStatementExecutor.cs ~Zeile 4265 ff.):
```csharp
var item = row.FirstOrDefault(item => item.Key.Equals(normalized, OrdinalIgnoreCase));
```
Das ist O(n) für jede Spalte, auf jeder Zeile.

**Fix:** Nach Phase 2 entfällt dieser Code vollständig (Ordinal-Zugriff). Bis dahin: vorberechnete `Dictionary<string, int>` Ordinal-Map als Zwischenschritt.

### 3b. Regex-Early-Exit in `NormalizeCompatibilityPaging` ✅

Die Methode wird auf jede Query angewendet, auch wenn kein `TOP`/`LIMIT` enthalten ist. Einfacher String-Check vor dem Regex-Match:

```csharp
if (!sql.Contains("TOP", StringComparison.OrdinalIgnoreCase) &&
    !sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
    return sql;
// ... erst dann Regex
```

### 3c. Streaming-Reader für EF Core aktivieren ⏳ Offen

`LayeredSqlDbDataReader` unterstützt `materializeEagerly: false`, aber EF Core nutzt es nicht standardmäßig. Wenn EF Core-Queries große Result-Sets zurückliefern (z. B. `Include` mit 1-zu-N), kann Streaming den Peak-Speicher stark reduzieren. Klären ob `LayeredSqlRelationalConnection` das Flag setzen kann.

---

## Phase 4 — Parse/Plan-Cache-Lücken schließen ✅ Teilweise implementiert

> Cache-Miss-Telemetrie (Diagnostics-Counter) in `SqlExecutionParser` und `InProcessSqlClientSession` ergänzt. Two-Level-Cache-Strategie noch offen.

### 4a. Cache-Miss-Telemetrie + Two-Level-Cache ✅ (Telemetrie) / ⏳ (Two-Level)

Aktuell: `SqlExecutionParser.s_resolverFreeCache` (statisch, LRU, für resolver-freie Statements) und `InProcessSqlClientSession._resolverDependentStatementCache` (instanzbezogen). 

Probleme:
- BETWEEN-/Range-Scan-Statements landen nicht im statischen Cache (IIndex-Referenz eingebettet)
- EF Core mit Pagination (`TOP`/`LIMIT`) bekommt nach `NormalizeCompatibilityPaging` zwar konsistente Keys, aber nur im Instanz-Cache

**Schritt:** Cache-Miss-Counter (Diagnostics) ergänzen, um Hot-Misses zu identifizieren. Dann: Two-Level-Strategie — statischer Cache für reine Syntax-Plans, Instanz-Cache für Pläne mit Index-Bindung.

### 4b. EF Core prepared statement reuse für JOIN + GROUP BY ⏳ Offen

EF Core sendet JOINs mit wechselnden Parameter-Werten, aber gleicher Struktur. Prüfen ob der Instanz-Level-Cache (Session-Dictionary) korrekt trifft oder der Normalisierungsschritt zu variablen Keys führt.

---

## Phase 5 — Verbleibende Join-Zeit-Lücke ✅ Teilweise implementiert

### 5a. Nachmessen nach Phase 2 + 3 ✅ Gemessen

Ergebnis: SelectJoin 5 134 μs (Baseline: 5 358 μs) — nur -4.2%. Der strukturelle Engpass liegt nicht im Projektion-Pfad, sondern im Index-Scan und Join-Verarbeitung des Storage-Layers.

### 5b. Covering-Index-Pfad für Join-Basis-Scans ✅

Wenn der Join-Scan über einen Index läuft (z. B. FK-Scan), sollte der gesamte Projektion-Wert aus dem Index lesbar sein ohne Table-Lookup. Prüfen ob `WalhallaIndex` diesen Covering-Pfad schon unterstützt.

### 5c. EF Core UNION ALL (TPH/TPC) durch optimierte Scan-Pfade ⏳ Offen

EF Core erzeugt bei Table-per-Hierarchy ein UNION ALL über alle konkreten Typen. Prüfen ob jeder UNION-Arm einzeln optimiert wird oder ob der gemeinsame Optimizer-Schritt greift.

---

## Zielwerte (nach allen 5 Phasen + Todo-Runde)

| Metrik                        | Baseline  | Phase 5b       | Todo-Runde (Ist) | Ziel      | Status |
|-------------------------------|----------:|---------------:|-----------------:|----------:|:-------|
| SelectById Latenz             | 65.87 μs  | 47.58 μs       | **48.19 μs**     | —         | ✅ -27% |
| SelectById Alloc              | 59.78 KB  | 47.13 KB       | **46.79 KB**     | < 5 KB    | ❌ -22% |
| SelectRange Latenz            | 658.54 μs | 651.10 μs      | **515.88 μs**    | —         | ✅ -22% |
| SelectRange Alloc             | 787.88 KB | 775.25 KB      | **540.44 KB**    | < 50 KB   | ❌ -31% |
| SelectJoin Zeit               | 5 357 μs  | 5 134 μs       | **5 475 μs**     | < 1500 μs | ❌ ±Rauschen |
| SelectJoin Alloc              | 6 432 KB  | 6 424 KB       | **6 280 KB**     | < 500 KB  | ❌ -2% |
| EF Core Include (N Zeilen)    | (TBD)     | —              | —                | < 2x SQLite | — |

**Analyse:** Alle Allokationsziele wurden verfehlt. Die RowBuffer-Optimierung hat den Projektion-Pfad entlastet (erklärt die Latenzverbesserung bei SelectById), aber die dominante Allokationsquelle liegt tiefer im Stack: Jede Zeile wird aus dem Storage-Layer als `Dictionary<string, object?>` materialisiert, bevor sie in den Projektion-Pfad gelangt. Das ist die nächste Baustelle.

**Nächste Optimierungsrunde (empfohlen):**
1. **Storage-Layer-Materialisierung**: Row-Deserialisierung in Walhalla.Storage direkt auf `object?[]` oder Span-based umstellen (eliminiert die Dictionary-Allokation an der Quelle)
2. **Object-Pooling**: Für häufig allokierte Zwischenpuffer (Scanner-Buffer, Join-Buckets)
3. **Typed Storage** (Phase 2c): `int` / `long` / `double` ohne Boxing in `RowBuffer.Values`

---

## Reihenfolge-Empfehlung

```
Phase 1a → Phase 2 → Phase 3 → Phase 1b (EF Core Bench) → Phase 4 → Phase 5
```

Phase 1a (In-Memory-Benchmarks) zuerst, um den I/O-Anteil aus dem Profil zu eliminieren. Dann sofort Phase 2, da sie den größten Allokations-Gewinn liefert. Phase 3 ist schnell umsetzbar und hat keine Abhängigkeiten. Phase 1b (EF Core Bench) direkt danach, damit Phase 4 und 5 mit echten Zahlen getrieben werden.

---

## Relevante Dateien (Schnellreferenz)

| Datei | Relevanz |
|---|---|
| `LayeredSql.Benchmarks/Program.cs` | Benchmark-Klassen (Phase 1a) |
| `LayeredSql/SqlStatementExecutor.cs` | Projektion ~Zeile 3420, TryGetValueFromRow ~Zeile 4265 (Phase 2, 3a) |
| `LayeredSql.AdoNet/LayeredSqlDbDataReader.cs` | GetValue, Zeilen-Materialisierung (Phase 2b, 3c) |
| `LayeredSql/Runtime/SqlExecutionParser.cs` | s_resolverFreeCache, ParseForExecution (Phase 4a) |
| `LayeredSql/SqlExecutionCache.cs` | LRU-Cache, Invalidierung (Phase 4a) |
| `LayeredSql.AdoNet/SqlClient/InProcessSqlClientSession.cs` | _resolverDependentStatementCache, NormalizeCompatibilityPaging (Phase 3b, 4b) |
| `LayeredSql/EngineProvider.cs` | InMemory() ~Zeile 80 (Phase 1a) |
| `LayeredSql.EfCore/LayeredSqlRelationalConnection.cs` | EF Core ADO-Pfad (Phase 1b, 3c) |
| `BenchmarkSuite1/Program.cs` | Executor-Level-Benchmarks (Phase 1b) |
