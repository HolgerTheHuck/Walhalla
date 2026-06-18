using System;
using System.Linq;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// C.7.4 integration tests: statistics-based tie-breaking in IndexSelector.SelectBestIndex.
/// </summary>
public class IndexSelectorStatisticsTests
{
    // ── C.7.4.1: With ANALYZE, the more selective index wins a tie ────────────

    [Fact]
    public void WithStats_TieBreaking_PicksMoreSelectiveIndex()
    {
        // Two single-column BTree indexes that each match one equality predicate.
        // Both score 10 (1 equality match, non-unique, non-covering).
        // After ANALYZE: Status has 2 distinct values (selectivity ≈ 0.5),
        //                UserId has 10 distinct values (selectivity ≈ 0.1).
        // Expected: idx_user wins because 0.1 < 0.5.
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, Status STRING, UserId INT)");
        engine.Execute("CREATE INDEX idx_status ON Orders (Status)");
        engine.Execute("CREATE INDEX idx_user ON Orders (UserId)");

        for (int i = 1; i <= 10; i++)
        {
            string status = i <= 5 ? "A" : "B";
            engine.Execute($"INSERT INTO Orders (Id, Status, UserId) VALUES ({i}, '{status}', {i})");
        }

        engine.Execute("ANALYZE Orders");

        var result = engine.Execute("EXPLAIN SELECT Id FROM Orders WHERE Status = 'A' AND UserId = 5");

        var indexScanRow = result.Rows.FirstOrDefault(r => (r["Operation"] as string) == "INDEX_SCAN");
        Assert.NotNull(indexScanRow);

        var details = indexScanRow!["Details"] as string;
        Assert.NotNull(details);
        Assert.Contains("idx_user", details);
        Assert.DoesNotContain("idx_status", details);
    }

    // ── C.7.4.2: Without ANALYZE, first-matched index used but query stays correct ─

    [Fact]
    public void WithoutStats_NoAnalyze_QueryReturnsCorrectRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, Status STRING, UserId INT)");
        engine.Execute("CREATE INDEX idx_status ON Orders (Status)");
        engine.Execute("CREATE INDEX idx_user ON Orders (UserId)");

        for (int i = 1; i <= 10; i++)
        {
            string status = i <= 5 ? "A" : "B";
            engine.Execute($"INSERT INTO Orders (Id, Status, UserId) VALUES ({i}, '{status}', {i})");
        }

        // No ANALYZE — planner uses structural score only; first-matched index wins tie.
        var result = engine.Execute("SELECT Id FROM Orders WHERE Status = 'A' AND UserId = 5");

        // Regardless of which index is chosen, result must be correct.
        Assert.Equal(1, result.Rows.Count);
        Assert.Equal(5, Convert.ToInt32(result.Rows[0]["Id"]));
    }

    // ── C.7.4.3: No tie — clear structural winner ignores statistics ──────────

    [Fact]
    public void WithStats_NoTie_HigherScoringIndexStillWins()
    {
        // idx_composite matches two equality predicates (score=20);
        // idx_user matches one (score=10). No tie → idx_composite always wins.
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, Status STRING, UserId INT)");
        engine.Execute("CREATE INDEX idx_user ON Orders (UserId)");
        engine.Execute("CREATE INDEX idx_composite ON Orders (Status, UserId)");

        for (int i = 1; i <= 10; i++)
        {
            string status = i <= 5 ? "A" : "B";
            engine.Execute($"INSERT INTO Orders (Id, Status, UserId) VALUES ({i}, '{status}', {i})");
        }

        engine.Execute("ANALYZE Orders");

        var result = engine.Execute("EXPLAIN SELECT Id FROM Orders WHERE Status = 'A' AND UserId = 5");

        var indexScanRow = result.Rows.FirstOrDefault(r => (r["Operation"] as string) == "INDEX_SCAN");
        Assert.NotNull(indexScanRow);

        var details = indexScanRow!["Details"] as string;
        Assert.NotNull(details);
        Assert.Contains("idx_composite", details);
        Assert.DoesNotContain("idx_user", details);
    }
}
