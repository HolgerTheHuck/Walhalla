using System;
using System.Linq;
using WalhallaSql.Sql;
using WalhallaSql.Statistics;
using Xunit;

namespace WalhallaSql.Tests;

public class StatisticsBuilderTests
{
    // ── ANALYZE SQL via WalhallaEngine (integration) ─────────────────────────

    [Fact]
    public void Analyze_EmptyTable_ReturnsZeroRowCount()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");

        var result = engine.Execute("ANALYZE T");

        Assert.Equal(1, result.AffectedRows);

        // Statistics should be available and reflect 0 rows
        var stats = engine.GetStatistics("T");
        Assert.NotNull(stats);
        Assert.Equal(0, stats!.RowCount);
    }

    [Fact]
    public void Analyze_KnownData_CorrectRowCount()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'A')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'B')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (3, 'A')");

        engine.Execute("ANALYZE T");

        var stats = engine.GetStatistics("T");
        Assert.NotNull(stats);
        Assert.Equal(3, stats!.RowCount);
    }

    [Fact]
    public void Analyze_NullValues_CorrectNullFraction()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, NULL)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, NULL)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 'hello')");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (4, 'world')");

        engine.Execute("ANALYZE T");

        var stats = engine.GetStatistics("T");
        Assert.NotNull(stats);
        Assert.True(stats!.Columns.TryGetValue("Val", out var col));
        // 2 of 4 rows are null → NullFraction = 0.5
        Assert.Equal(0.5, col.NullFraction, precision: 6);
    }

    [Fact]
    public void Analyze_DistinctValues_CorrectDistinctCount()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Cat INT)");
        // 5 rows, 3 distinct Cat values
        engine.Execute("INSERT INTO T (Id, Cat) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Cat) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Cat) VALUES (3, 10)");
        engine.Execute("INSERT INTO T (Id, Cat) VALUES (4, 30)");
        engine.Execute("INSERT INTO T (Id, Cat) VALUES (5, 20)");

        engine.Execute("ANALYZE T");

        var stats = engine.GetStatistics("T");
        Assert.True(stats!.Columns.TryGetValue("Cat", out var col));
        Assert.Equal(3.0, col.DistinctCount, precision: 6);
    }

    [Fact]
    public void Analyze_MostCommonValues_TopValueHasHighestFrequency()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Cat STRING)");
        // 'A' appears 4 times, 'B' 2 times, 'C' 1 time
        for (int i = 1; i <= 4; i++)
            engine.Execute($"INSERT INTO T (Id, Cat) VALUES ({i}, 'A')");
        engine.Execute("INSERT INTO T (Id, Cat) VALUES (5, 'B')");
        engine.Execute("INSERT INTO T (Id, Cat) VALUES (6, 'B')");
        engine.Execute("INSERT INTO T (Id, Cat) VALUES (7, 'C')");

        engine.Execute("ANALYZE T");

        var stats = engine.GetStatistics("T");
        Assert.True(stats!.Columns.TryGetValue("Cat", out var col));
        Assert.NotEmpty(col.MostCommonValues);

        // Top MCV entry should be 'A' with frequency 4/7
        var top = col.MostCommonValues[0];
        Assert.Equal("A", top.Value);
        Assert.InRange(top.Frequency, 0.57, 0.58);
    }

    [Fact]
    public void Analyze_AllTables_ReturnsAffectedCount()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY)");
        engine.Execute("CREATE TABLE C (Id INT PRIMARY KEY)");

        var result = engine.Execute("ANALYZE");

        // Should have analyzed all 3 tables (plus any system tables)
        Assert.True(result.AffectedRows >= 3);
    }

    [Fact]
    public void Analyze_SpecificTable_OnlyAffectsThatTable()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE X (Id INT PRIMARY KEY)");
        engine.Execute("CREATE TABLE Y (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO X (Id) VALUES (1)");

        var result = engine.Execute("ANALYZE X");

        Assert.Equal(1, result.AffectedRows);

        // X should have stats, Y should not
        var statsX = engine.GetStatistics("X");
        Assert.NotNull(statsX);
    }

    [Fact]
    public void Analyze_UnknownTable_ThrowsWalhallaException()
    {
        using var engine = WalhallaEngine.InMemory();

        Assert.Throws<WalhallaException>(() => engine.Execute("ANALYZE NonExistentTable"));
    }

    [Fact]
    public void Analyze_StringColumn_AverageWidthIsPositive()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'short')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'a longer string value')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (3, 'medium length')");

        engine.Execute("ANALYZE T");

        var stats = engine.GetStatistics("T");
        Assert.True(stats!.Columns.TryGetValue("Name", out var col));
        Assert.True(col.AverageWidth > 0);
    }

    [Fact]
    public void Analyze_HistogramBuilt_ForEnoughDistinctValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Score INT)");
        // Insert 100 distinct values to trigger histogram construction
        for (int i = 1; i <= 100; i++)
            engine.Execute($"INSERT INTO T (Id, Score) VALUES ({i}, {i * 10})");

        engine.Execute("ANALYZE T");

        var stats = engine.GetStatistics("T");
        Assert.True(stats!.Columns.TryGetValue("Score", out var col));
        // With 100 distinct values histogram should be non-empty
        Assert.NotEmpty(col.Histogram);
    }

    [Fact]
    public void Analyze_UpdatesExistingStats_OnReAnalyze()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 1)");

        engine.Execute("ANALYZE T");
        var stats1 = engine.GetStatistics("T");
        Assert.Equal(1, stats1!.RowCount);

        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 2)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 3)");

        engine.Execute("ANALYZE T");
        var stats2 = engine.GetStatistics("T");
        Assert.Equal(3, stats2!.RowCount);
    }
}
