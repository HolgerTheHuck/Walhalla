using System;
using System.Linq;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// C.7.5/C.7.6 integration tests: statistics-based row estimates and join strategy.
/// </summary>
public class ExplainStatisticsTests
{
    // ── C.7.5: JOIN strategy changes after ANALYZE ────────────────────────────

    [Fact]
    public void JoinExplain_StrategyChangesAfterAnalyze()
    {
        // A has 200 rows (Val 1..200). RIGHT JOIN B (10 rows) on a.Id = b.AId.
        // b.AId is not a primary key → sort-merge bypass skipped → size-based rule.
        // For RIGHT JOIN buildCount = estLeft (left-side cardinality).
        //
        // Before ANALYZE A: estLeft = 200 (raw count) > 100 → HASH_JOIN (RIGHT)
        // After  ANALYZE A: estLeft ≈ 9 (Val < 10 out of 200) ≤ 100 → NESTED_LOOP_JOIN (RIGHT)
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, AId INT)");

        for (int i = 1; i <= 200; i++)
            engine.Execute($"INSERT INTO A (Id, Val) VALUES ({i}, {i})");
        for (int i = 1; i <= 10; i++)
            engine.Execute($"INSERT INTO B (Id, AId) VALUES ({i}, {i})");

        const string query = "EXPLAIN SELECT a.Id FROM A a RIGHT JOIN B b ON a.Id = b.AId WHERE a.Val < 10";

        var beforeOps = engine.Execute(query)
            .Rows.Select(r => r["Operation"] as string).ToList();

        engine.Execute("ANALYZE A");

        var afterOps = engine.Execute(query)
            .Rows.Select(r => r["Operation"] as string).ToList();

        // Before: raw count 200 → hash build
        Assert.Contains("HASH_JOIN (RIGHT)", beforeOps!);
        Assert.DoesNotContain("NESTED_LOOP_JOIN (RIGHT)", beforeOps!);

        // After: stats-estimated ≈9 rows → nested-loop build
        Assert.Contains("NESTED_LOOP_JOIN (RIGHT)", afterOps!);
        Assert.DoesNotContain("HASH_JOIN (RIGHT)", afterOps!);
    }

    // ── C.7.6: SCAN Details carries est_rows annotation ──────────────────────

    [Fact]
    public void ScanExplain_EstRowsChangesAfterAnalyze()
    {
        // T has 100 rows (Val 1..100). WHERE Val < 5 → ~4% selectivity.
        // Val is not indexed → full table scan path.
        //
        // Before ANALYZE: no stats → est_rows equals raw row count (100).
        // After  ANALYZE: histogram-based estimate → est_rows ≈ 4.
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");

        for (int i = 1; i <= 100; i++)
            engine.Execute($"INSERT INTO T (Id, Val) VALUES ({i}, {i})");

        const string query = "EXPLAIN SELECT Id FROM T WHERE Val < 5";

        var beforeScan = engine.Execute(query)
            .Rows.First(r => (r["Operation"] as string) == "SCAN");
        var beforeDetails = beforeScan["Details"] as string;

        engine.Execute("ANALYZE T");

        var afterScan = engine.Execute(query)
            .Rows.First(r => (r["Operation"] as string) == "SCAN");
        var afterDetails = afterScan["Details"] as string;

        // Both annotated with est_rows=~N
        Assert.NotNull(beforeDetails);
        Assert.Contains("est_rows=~", beforeDetails);
        Assert.NotNull(afterDetails);
        Assert.Contains("est_rows=~", afterDetails);

        // Without stats: est_rows equals the raw row count
        Assert.Contains("est_rows=~100", beforeDetails);

        // After ANALYZE: selectivity-based estimate is significantly smaller
        Assert.DoesNotContain("est_rows=~100", afterDetails);
    }
}
