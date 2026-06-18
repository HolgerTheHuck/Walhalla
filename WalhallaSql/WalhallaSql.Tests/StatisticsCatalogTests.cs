using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql.Statistics;
using Xunit;

namespace WalhallaSql.Tests;

public class StatisticsCatalogTests
{
    // ── StatisticsCatalog unit tests ───────────────────────────────────────────

    [Fact]
    public void TryGet_EmptyCatalog_ReturnsFalse()
    {
        var catalog = new StatisticsCatalog();
        Assert.False(catalog.TryGet(1, out _));
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsStoredStats()
    {
        var catalog = new StatisticsCatalog();
        var stats = new TableStatistics { RowCount = 500, AnalyzedAt = DateTime.UtcNow };

        catalog.Set(42, stats);

        Assert.True(catalog.TryGet(42, out var result));
        Assert.Equal(500, result.RowCount);
    }

    [Fact]
    public void Set_OverwritesExistingStats()
    {
        var catalog = new StatisticsCatalog();
        catalog.Set(1, new TableStatistics { RowCount = 100 });
        catalog.Set(1, new TableStatistics { RowCount = 200 });

        Assert.True(catalog.TryGet(1, out var result));
        Assert.Equal(200, result.RowCount);
    }

    [Fact]
    public void Invalidate_RemovesStats()
    {
        var catalog = new StatisticsCatalog();
        catalog.Set(7, new TableStatistics { RowCount = 99 });

        catalog.Invalidate(7);

        Assert.False(catalog.TryGet(7, out _));
    }

    [Fact]
    public void Invalidate_NonExistentTable_DoesNotThrow()
    {
        var catalog = new StatisticsCatalog();
        var ex = Record.Exception(() => catalog.Invalidate(999));
        Assert.Null(ex);
    }

    [Fact]
    public void InvalidateAll_ClearsAllEntries()
    {
        var catalog = new StatisticsCatalog();
        catalog.Set(1, new TableStatistics { RowCount = 1 });
        catalog.Set(2, new TableStatistics { RowCount = 2 });
        catalog.Set(3, new TableStatistics { RowCount = 3 });

        catalog.InvalidateAll();

        Assert.False(catalog.TryGet(1, out _));
        Assert.False(catalog.TryGet(2, out _));
        Assert.False(catalog.TryGet(3, out _));
    }

    [Fact]
    public void TryGet_DifferentTableIds_AreIndependent()
    {
        var catalog = new StatisticsCatalog();
        catalog.Set(10, new TableStatistics { RowCount = 10 });
        catalog.Set(20, new TableStatistics { RowCount = 20 });

        Assert.True(catalog.TryGet(10, out var r10));
        Assert.True(catalog.TryGet(20, out var r20));
        Assert.Equal(10, r10.RowCount);
        Assert.Equal(20, r20.RowCount);
        Assert.False(catalog.TryGet(30, out _));
    }

    [Fact]
    public void ColumnStatistics_DefaultValues_AreEmpty()
    {
        var col = new ColumnStatistics();
        Assert.Equal(0.0, col.NullFraction);
        Assert.Equal(0.0, col.DistinctCount);
        Assert.Equal(0, col.AverageWidth);
        Assert.Empty(col.MostCommonValues);
        Assert.Empty(col.Histogram);
    }

    [Fact]
    public void TableStatistics_WithColumns_ColumnLookupIsCaseInsensitive()
    {
        var col = new ColumnStatistics { NullFraction = 0.1, DistinctCount = 50 };
        var stats = new TableStatistics
        {
            RowCount = 1000,
            Columns = new Dictionary<string, ColumnStatistics>(StringComparer.OrdinalIgnoreCase)
            {
                ["MyColumn"] = col
            }
        };

        Assert.True(stats.Columns.ContainsKey("mycolumn"));
        Assert.True(stats.Columns.ContainsKey("MYCOLUMN"));
        Assert.Equal(0.1, stats.Columns["MyColumn"].NullFraction);
    }

    [Fact]
    public void StatisticsCatalog_ConcurrentSetAndGet_ThreadSafe()
    {
        var catalog = new StatisticsCatalog();
        const int threadCount = 8;
        const int iterations = 1000;

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    int tableId = threadId % 4; // contention on 4 shared tables
                    catalog.Set(tableId, new TableStatistics { RowCount = i });
                    catalog.TryGet(tableId, out _);
                    if (i % 10 == 0)
                        catalog.Invalidate(tableId);
                }
            });
        }

        var ex = Record.Exception(() => Task.WaitAll(tasks));
        Assert.Null(ex);
    }
}
