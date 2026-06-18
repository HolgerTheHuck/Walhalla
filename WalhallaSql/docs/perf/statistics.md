# Statistics & ANALYZE — Performance Notes

## Benchmark: `StatisticsAnalyzeBenchmark`

Measures the effect of running `ANALYZE` before executing queries on a
**strongly skewed dataset** (10 000 rows; `Region` column: 1 % "R1", 99 % "R0").

### How to Run

```bash
cd WalhallaSql.Benchmarks
dotnet run -c Release -- --filter "*StatisticsAnalyzeBenchmark*"
```

### What Is Measured

| Method              | Description |
|---------------------|-------------|
| `SelectRareRegion`  | `WHERE Region = 'R1'` — returns ~100 rows; benefits most from accurate MCV statistics |
| `SelectCommonRegion`| `WHERE Region = 'R0'` — returns ~9 900 rows; planner should prefer scan |
| `JoinWithSkewedFilter` | nested-loop vs hash strategy guided by `est_rows` |

The benchmark is parameterised on `UseAnalyze` (`false` / `true`) to produce
a direct comparison.

### Expected Outcome

- **`UseAnalyze = false`**: default selectivity constant (~10 %) for all
  equality predicates → planner may choose a sub-optimal plan for the rare value.
- **`UseAnalyze = true`**: real MCV frequencies (1 % / 99 %) → planner picks
  index seek for the rare value and full scan for the dominant value.

### Observability

After running `ANALYZE`, diagnostic counters are updated atomically:

| Property on `WalhallaEngine` | Meaning |
|------------------------------|---------|
| `AnalyzeTableCount`          | Cumulative tables processed by all ANALYZE commands |
| `AnalyzeDurationMs`          | Cumulative wall-clock time spent in ANALYZE (ms) |
| `EstimatorHits`              | Planner lookups that found real statistics |
| `EstimatorFallbacks`         | Planner lookups that fell back to default constants |

The same values are emitted as .NET metrics via `System.Diagnostics.Metrics`
(meter name `"WalhallaSql"`), and as OpenTelemetry spans via `ActivitySource("WalhallaSql")`.
