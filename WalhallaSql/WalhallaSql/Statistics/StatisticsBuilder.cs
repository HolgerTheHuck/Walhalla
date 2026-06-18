using System;
using System.Collections.Generic;
using WalhallaSql.Sql;
using WalhallaSql.Storage;

namespace WalhallaSql.Statistics;

/// <summary>
/// Builds <see cref="TableStatistics"/> by performing a full-scan of a table.
/// </summary>
internal static class StatisticsBuilder
{
    private const int MaxMcvSlots = 32;
    private const int HistogramBuckets = 64;

    /// <summary>
    /// Performs a full scan of <paramref name="tableId"/> and returns freshly computed statistics.
    /// </summary>
    internal static TableStatistics Build(int tableId, TableStore store, SqlTableDefinition tableDef)
    {
        var rows = new List<object?[]>();
        RowDecoder decoder = encoded => RowCodec.DecodeToArray(encoded, tableDef);
        store.ScanWithPredicate(tableId, decoder, predicate: null, rows);

        long rowCount = rows.Count;
        var columns = new Dictionary<string, ColumnStatistics>(StringComparer.OrdinalIgnoreCase);

        for (int colIdx = 0; colIdx < tableDef.Columns.Count; colIdx++)
        {
            var colDef = tableDef.Columns[colIdx];
            columns[colDef.Name] = ComputeColumnStats(rows, colIdx, rowCount);
        }

        return new TableStatistics
        {
            RowCount = rowCount,
            AnalyzedAt = DateTime.UtcNow,
            Columns = columns
        };
    }

    private static ColumnStatistics ComputeColumnStats(List<object?[]> rows, int colIdx, long rowCount)
    {
        if (rowCount == 0)
        {
            return new ColumnStatistics
            {
                NullFraction = 0,
                DistinctCount = 0,
                AverageWidth = 0,
                MostCommonValues = [],
                Histogram = []
            };
        }

        long nullCount = 0;
        long totalWidth = 0;
        var valueCounts = new Dictionary<object, int>(ValueEqualityComparer.Instance);

        foreach (var row in rows)
        {
            var val = row[colIdx];
            if (val == null)
            {
                nullCount++;
                continue;
            }

            totalWidth += EstimateWidth(val);

            if (valueCounts.TryGetValue(val, out int cnt))
                valueCounts[val] = cnt + 1;
            else
                valueCounts[val] = 1;
        }

        long nonNullCount = rowCount - nullCount;
        double nullFraction = rowCount > 0 ? (double)nullCount / rowCount : 0.0;
        int averageWidth = nonNullCount > 0 ? (int)(totalWidth / nonNullCount) : 0;
        double distinctCount = valueCounts.Count;

        // Build MCV: top MaxMcvSlots entries by frequency
        var sortedByFreq = new List<KeyValuePair<object, int>>(valueCounts);
        sortedByFreq.Sort((a, b) => b.Value.CompareTo(a.Value));

        int mcvCount = Math.Min(MaxMcvSlots, sortedByFreq.Count);
        var mcv = new (object Value, double Frequency)[mcvCount];
        var mcvSet = new HashSet<object>(ValueEqualityComparer.Instance);

        for (int i = 0; i < mcvCount; i++)
        {
            var kvp = sortedByFreq[i];
            double freq = rowCount > 0 ? (double)kvp.Value / rowCount : 0.0;
            mcv[i] = (kvp.Key, freq);
            mcvSet.Add(kvp.Key);
        }

        // Build histogram from non-MCV, non-null values
        var histValues = new List<object>(capacity: (int)nonNullCount);
        foreach (var row in rows)
        {
            var val = row[colIdx];
            if (val == null) continue;
            if (mcvSet.Contains(val)) continue;
            histValues.Add(val);
        }

        object[] histogram = BuildHistogram(histValues);

        return new ColumnStatistics
        {
            NullFraction = nullFraction,
            DistinctCount = distinctCount,
            AverageWidth = averageWidth,
            MostCommonValues = mcv,
            Histogram = histogram
        };
    }

    private static object[] BuildHistogram(List<object> values)
    {
        if (values.Count < 2)
            return [];

        values.Sort(ValueOrderComparer.Instance);

        // Equi-depth: pick HistogramBuckets+1 boundary points
        int buckets = Math.Min(HistogramBuckets, values.Count);
        var boundaries = new object[buckets + 1];
        boundaries[0] = values[0];
        boundaries[buckets] = values[values.Count - 1];

        for (int b = 1; b < buckets; b++)
        {
            int idx = (int)Math.Round((double)b * values.Count / buckets);
            idx = Math.Clamp(idx, 0, values.Count - 1);
            boundaries[b] = values[idx];
        }

        return boundaries;
    }

    private static int EstimateWidth(object val) => val switch
    {
        string s => s.Length * 2,
        _ => 8
    };

    // Equality comparer that handles cross-type numeric equality
    private sealed class ValueEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ValueEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            if (x.GetType() == y.GetType()) return x.Equals(y);
            return x.ToString() == y.ToString();
        }

        public int GetHashCode(object obj) =>
            obj is string s ? StringComparer.Ordinal.GetHashCode(s) : obj.GetHashCode();
    }

    // Order comparer for histogram sorting
    private sealed class ValueOrderComparer : IComparer<object>
    {
        public static readonly ValueOrderComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            if (x.GetType() == y.GetType() && x is IComparable cx)
                return cx.CompareTo(y);
            return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
        }
    }
}
