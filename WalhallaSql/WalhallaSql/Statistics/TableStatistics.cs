using System;
using System.Collections.Generic;

namespace WalhallaSql.Statistics;

/// <summary>Per-table statistics collected by ANALYZE.</summary>
public sealed record TableStatistics
{
    /// <summary>Total live row count at the time ANALYZE ran.</summary>
    public long RowCount { get; init; }

    /// <summary>Timestamp when ANALYZE was last executed for this table.</summary>
    public DateTime AnalyzedAt { get; init; }

    /// <summary>
    /// Per-column statistics keyed by column name (case-insensitive).
    /// Columns not present here have no statistics (planner falls back to heuristics).
    /// </summary>
    public IReadOnlyDictionary<string, ColumnStatistics> Columns { get; init; }
        = new Dictionary<string, ColumnStatistics>(StringComparer.OrdinalIgnoreCase);
}
