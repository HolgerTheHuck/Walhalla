# WalhallaSql vs SQLite Comparison Report

> Generated: 2026-06-02
> BenchmarkDotNet JSON: *(run `scripts/run-sqlite-comparison.ps1` to generate)*

| Workload | SQLite | WalhallaSql | Ratio | Status |
|----------|--------:|------------:|------:|--------|
| InsertBatch (10k in Tx) | — | — | — | ⏭️ pending |
| BulkInsert (100k rows) | — | — | — | ⏭️ pending |
| SelectById (10k PK lookups) | — | — | — | ⏭️ pending |
| RangeScan (index range 10k rows) | — | — | — | ⏭️ pending |
| JoinTwoTables (10k × 10k hash join) | — | — | — | ⏭️ pending |
| Aggregate GROUP BY (100k rows) | — | — | — | ⏭️ pending |
| Mixed Read/Write (4 threads, 70/30) | — | — | — | ⏭️ pending |

## Legend

- **Ratio < 2.0x** → ✅ within target (WalhallaSql within ±2× of SQLite)
- **Ratio 2.0–5.0x** → ⚠️ acceptable but not ideal
- **Ratio > 5.0x** → ❌ needs investigation

## Running the Comparison

```powershell
.\scripts\run-sqlite-comparison.ps1
```

This executes BenchmarkDotNet for all comparison workloads and overwrites this file with measured results.
