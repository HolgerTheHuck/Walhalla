# ANALYZE & Cost-Based Query Planner

WalhallaSql ships a cost-based query planner that uses per-table column statistics
to estimate result cardinalities (`est_rows`) and choose optimal index and join strategies.
This document explains how to populate and use those statistics.

## Overview

| Component | Role |
|-----------|------|
| `ANALYZE` SQL statement | Scans all (or one) table, builds histograms + MCVs |
| `StatisticsBuilder` | Computes `TableStatistics` (row count, null fraction, MCVs, histogram) |
| `StatisticsCatalog` | Thread-safe in-memory store; survives the session |
| `SelectivityEstimator` | Converts a predicate + stats into an estimated row fraction |
| `IndexSelector` | Uses `SelectivityEstimator` to pick between competing indexes |
| `JoinStrategyEstimator` | Uses `est_rows` to select nested-loop, hash, or sort-merge join |

## Running ANALYZE

```sql
-- All user tables
ANALYZE;

-- Single table
ANALYZE Customers;
```

ANALYZE is idempotent and safe to re-run at any time.
It holds no locks beyond what a normal table scan requires.

### When to Run

- After bulk-loading large amounts of data (`InsertBatch`).
- After a significant number of `INSERT`, `UPDATE`, or `DELETE` operations.
- Before running EXPLAIN to see accurate `est_rows` values.

## Reading Estimates with EXPLAIN

```sql
EXPLAIN SELECT Id, Name FROM Customers WHERE Region = 'R1';
```

Output columns include `est_rows` — the planner's row-count estimate for each operator.
After `ANALYZE`, these values reflect real column statistics rather than defaults.

## Telemetry Counters

```csharp
var engine = WalhallaEngine.InMemory();
// ... set up schema and data ...
engine.Execute("ANALYZE");

Console.WriteLine($"Tables analyzed:      {engine.AnalyzeTableCount}");
Console.WriteLine($"Total ANALYZE time:   {engine.AnalyzeDurationMs} ms");
Console.WriteLine($"Estimator hits:       {engine.EstimatorHits}");   // real stats used
Console.WriteLine($"Estimator fallbacks:  {engine.EstimatorFallbacks}"); // default constants used
```

All four properties are thread-safe and updated atomically.

## .NET Observability (Metrics + Tracing)

WalhallaSql emits standard .NET observability signals from the outset.
No NuGet package required — subscribe with a `MeterListener` or an
OpenTelemetry SDK collector:

| Instrument | Type | Unit | Description |
|------------|------|------|-------------|
| `walhallasql.analyze.tables`      | Counter   | —    | Tables processed by ANALYZE |
| `walhallasql.analyze.duration_ms` | Histogram | ms   | Wall-clock time per ANALYZE |
| `walhallasql.estimator.hits`      | Counter   | —    | Planner lookups with real stats |
| `walhallasql.estimator.fallbacks` | Counter   | —    | Planner lookups using defaults |

Meter name: **`WalhallaSql`**  
ActivitySource name: **`WalhallaSql`**

### Example: listen with `MeterListener`

```csharp
var listener = new System.Diagnostics.Metrics.MeterListener();
listener.InstrumentPublished = (instrument, l) =>
{
    if (instrument.Meter.Name == "WalhallaSql") l.EnableMeasurementEvents(instrument);
};
listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
    Console.WriteLine($"{instrument.Name} += {value}"));
listener.Start();
```

## Statistics Persistence

Statistics are persisted to the WAL-backed store by `WalhallaEngine` after
every ANALYZE run. They are automatically reloaded when the engine opens an
existing database, so ANALYZE does not need to be re-run on every restart.

## See Also

- [`docs/perf/statistics.md`](perf/statistics.md) — benchmark results and how to run them
- `WalhallaEngine.PlanCacheHits` / `PlanCacheMisses` — plan-cache efficiency counters
