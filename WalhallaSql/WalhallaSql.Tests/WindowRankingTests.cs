using System;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// B.2.2 — Ranking window functions NTILE(n), PERCENT_RANK() and CUME_DIST().
/// Rows are inserted in ORDER BY order so the assertions verify the computed value of each
/// function independent of the engine's row-ordering behaviour.
/// </summary>
public class WindowRankingTests
{
    private static WalhallaEngine NewEngine()
    {
        var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Grp STRING, Val INT)");
        return engine;
    }

    [Fact]
    public void NTile_DistributesRowsIntoBuckets()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'A', 30)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (4, 'A', 40)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (5, 'A', 50)");

        var result = engine.Execute(
            "SELECT Val, NTILE(3) OVER (ORDER BY Val) AS bucket FROM T");

        // 5 rows / 3 buckets → sizes 2,2,1
        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(1L, result.Rows[0]["bucket"]);
        Assert.Equal(1L, result.Rows[1]["bucket"]);
        Assert.Equal(2L, result.Rows[2]["bucket"]);
        Assert.Equal(2L, result.Rows[3]["bucket"]);
        Assert.Equal(3L, result.Rows[4]["bucket"]);
    }

    [Fact]
    public void NTile_MoreBucketsThanRows_OneRowPerBucket()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");

        var result = engine.Execute(
            "SELECT Val, NTILE(5) OVER (ORDER BY Val) AS bucket FROM T");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1L, result.Rows[0]["bucket"]);
        Assert.Equal(2L, result.Rows[1]["bucket"]);
    }

    [Fact]
    public void NTile_WithPartition()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'B', 30)");

        var result = engine.Execute(
            "SELECT Grp, Val, NTILE(2) OVER (PARTITION BY Grp ORDER BY Val) AS bucket FROM T");

        Assert.Equal(3, result.Rows.Count);
        // Grp A (2 rows / 2 buckets) → 1,2 ; Grp B (1 row) → 1
        Assert.Equal(1L, result.Rows[0]["bucket"]);
        Assert.Equal(2L, result.Rows[1]["bucket"]);
        Assert.Equal(1L, result.Rows[2]["bucket"]);
    }

    [Fact]
    public void NTile_ZeroBuckets_Throws()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");

        Assert.ThrowsAny<Exception>(() =>
            engine.Execute("SELECT Val, NTILE(0) OVER (ORDER BY Val) AS bucket FROM T"));
    }

    [Fact]
    public void PercentRank_ComputesRelativeRank()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (4, 'A', 40)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (5, 'A', 50)");

        var result = engine.Execute(
            "SELECT Val, PERCENT_RANK() OVER (ORDER BY Val) AS pr FROM T");

        // rank: 1,2,2,4,5 → (rank-1)/4
        Assert.Equal(0.0, Convert.ToDouble(result.Rows[0]["pr"]), 5);
        Assert.Equal(0.25, Convert.ToDouble(result.Rows[1]["pr"]), 5);
        Assert.Equal(0.25, Convert.ToDouble(result.Rows[2]["pr"]), 5);
        Assert.Equal(0.75, Convert.ToDouble(result.Rows[3]["pr"]), 5);
        Assert.Equal(1.0, Convert.ToDouble(result.Rows[4]["pr"]), 5);
    }

    [Fact]
    public void PercentRank_SingleRow_IsZero()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");

        var result = engine.Execute(
            "SELECT Val, PERCENT_RANK() OVER (ORDER BY Val) AS pr FROM T");

        Assert.Single(result.Rows);
        Assert.Equal(0.0, Convert.ToDouble(result.Rows[0]["pr"]), 5);
    }

    [Fact]
    public void CumeDist_ComputesCumulativeDistribution()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (4, 'A', 40)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (5, 'A', 50)");

        var result = engine.Execute(
            "SELECT Val, CUME_DIST() OVER (ORDER BY Val) AS cd FROM T");

        // peers share the cumulative count at the group's last position
        Assert.Equal(0.2, Convert.ToDouble(result.Rows[0]["cd"]), 5);
        Assert.Equal(0.6, Convert.ToDouble(result.Rows[1]["cd"]), 5);
        Assert.Equal(0.6, Convert.ToDouble(result.Rows[2]["cd"]), 5);
        Assert.Equal(0.8, Convert.ToDouble(result.Rows[3]["cd"]), 5);
        Assert.Equal(1.0, Convert.ToDouble(result.Rows[4]["cd"]), 5);
    }

    [Fact]
    public void CumeDist_WithPartition()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'B', 30)");

        var result = engine.Execute(
            "SELECT Grp, Val, CUME_DIST() OVER (PARTITION BY Grp ORDER BY Val) AS cd FROM T");

        Assert.Equal(3, result.Rows.Count);
        // Grp A: 0.5, 1.0 ; Grp B: 1.0
        Assert.Equal(0.5, Convert.ToDouble(result.Rows[0]["cd"]), 5);
        Assert.Equal(1.0, Convert.ToDouble(result.Rows[1]["cd"]), 5);
        Assert.Equal(1.0, Convert.ToDouble(result.Rows[2]["cd"]), 5);
    }
}
