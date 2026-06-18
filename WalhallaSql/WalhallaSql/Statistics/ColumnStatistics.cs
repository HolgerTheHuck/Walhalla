using System;

namespace WalhallaSql.Statistics;

/// <summary>Per-column statistics collected by ANALYZE.</summary>
public sealed record ColumnStatistics
{
    /// <summary>Fraction of rows where this column is NULL (0.0–1.0).</summary>
    public double NullFraction { get; init; }

    /// <summary>
    /// Estimated number of distinct non-null values.
    /// Positive = absolute count; negative = fraction of total rows (PostgreSQL convention).
    /// </summary>
    public double DistinctCount { get; init; }

    /// <summary>Average storage width in bytes (used for cost estimates).</summary>
    public int AverageWidth { get; init; }

    /// <summary>Most-common values with their observed frequencies, sorted descending by frequency.</summary>
    public (object Value, double Frequency)[] MostCommonValues { get; init; } = [];

    /// <summary>
    /// Equi-depth histogram bucket boundaries (non-null values, sorted ascending).
    /// A bucket i covers values in [Histogram[i], Histogram[i+1]).
    /// </summary>
    public object[] Histogram { get; init; } = [];
}
