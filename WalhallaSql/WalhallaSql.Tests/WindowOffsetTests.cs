using System;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// B.2.4 — Offset/value window functions: LAG/LEAD/FIRST_VALUE/LAST_VALUE/NTH_VALUE.
/// Rows are inserted in ORDER BY order so assertions verify the computed value per row
/// independent of the engine's physical row ordering.
/// </summary>
public class WindowOffsetTests
{
    private static WalhallaEngine NewEngine()
    {
        var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Grp STRING, Val INT)");
        return engine;
    }

    private static void Seed(WalhallaEngine engine)
    {
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'A', 30)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (4, 'A', 40)");
    }

    [Fact]
    public void Lag_DefaultOffsetOne()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute("SELECT Val, LAG(Val) OVER (ORDER BY Val) AS p FROM T");

        Assert.Null(result.Rows[0]["p"]);
        Assert.Equal(10, Convert.ToInt32(result.Rows[1]["p"]));
        Assert.Equal(20, Convert.ToInt32(result.Rows[2]["p"]));
        Assert.Equal(30, Convert.ToInt32(result.Rows[3]["p"]));
    }

    [Fact]
    public void Lag_WithOffsetAndDefault()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute("SELECT Val, LAG(Val, 2, -1) OVER (ORDER BY Val) AS p FROM T");

        Assert.Equal(-1, Convert.ToInt32(result.Rows[0]["p"]));
        Assert.Equal(-1, Convert.ToInt32(result.Rows[1]["p"]));
        Assert.Equal(10, Convert.ToInt32(result.Rows[2]["p"]));
        Assert.Equal(20, Convert.ToInt32(result.Rows[3]["p"]));
    }

    [Fact]
    public void Lead_DefaultOffsetOne()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute("SELECT Val, LEAD(Val) OVER (ORDER BY Val) AS n FROM T");

        Assert.Equal(20, Convert.ToInt32(result.Rows[0]["n"]));
        Assert.Equal(30, Convert.ToInt32(result.Rows[1]["n"]));
        Assert.Equal(40, Convert.ToInt32(result.Rows[2]["n"]));
        Assert.Null(result.Rows[3]["n"]);
    }

    [Fact]
    public void Lead_WithOffsetAndDefault()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute("SELECT Val, LEAD(Val, 2, 0) OVER (ORDER BY Val) AS n FROM T");

        Assert.Equal(30, Convert.ToInt32(result.Rows[0]["n"]));
        Assert.Equal(40, Convert.ToInt32(result.Rows[1]["n"]));
        Assert.Equal(0, Convert.ToInt32(result.Rows[2]["n"]));
        Assert.Equal(0, Convert.ToInt32(result.Rows[3]["n"]));
    }

    [Fact]
    public void Lag_WithPartition()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'B', 100)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (4, 'B', 200)");

        var result = engine.Execute(
            "SELECT Grp, Val, LAG(Val) OVER (PARTITION BY Grp ORDER BY Val) AS p FROM T");

        Assert.Null(result.Rows[0]["p"]);                          // A, 10
        Assert.Equal(10, Convert.ToInt32(result.Rows[1]["p"]));    // A, 20
        Assert.Null(result.Rows[2]["p"]);                          // B, 100 (partition reset)
        Assert.Equal(100, Convert.ToInt32(result.Rows[3]["p"]));   // B, 200
    }

    [Fact]
    public void FirstValue_ReturnsPartitionStart()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute("SELECT Val, FIRST_VALUE(Val) OVER (ORDER BY Val) AS f FROM T");

        foreach (var row in result.Rows)
            Assert.Equal(10, Convert.ToInt32(row["f"]));
    }

    [Fact]
    public void LastValue_DefaultFrameIsCurrentRow()
    {
        using var engine = NewEngine();
        Seed(engine);

        // Default frame UNBOUNDED PRECEDING .. CURRENT ROW → LAST_VALUE equals the current row.
        var result = engine.Execute("SELECT Val, LAST_VALUE(Val) OVER (ORDER BY Val) AS l FROM T");

        Assert.Equal(10, Convert.ToInt32(result.Rows[0]["l"]));
        Assert.Equal(20, Convert.ToInt32(result.Rows[1]["l"]));
        Assert.Equal(30, Convert.ToInt32(result.Rows[2]["l"]));
        Assert.Equal(40, Convert.ToInt32(result.Rows[3]["l"]));
    }

    [Fact]
    public void LastValue_UnboundedFollowing_ReturnsPartitionEnd()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute(
            "SELECT Val, LAST_VALUE(Val) OVER (ORDER BY Val ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS l FROM T");

        foreach (var row in result.Rows)
            Assert.Equal(40, Convert.ToInt32(row["l"]));
    }

    [Fact]
    public void NthValue_ReturnsNthInFrame()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute(
            "SELECT Val, NTH_VALUE(Val, 2) OVER (ORDER BY Val ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS n FROM T");

        foreach (var row in result.Rows)
            Assert.Equal(20, Convert.ToInt32(row["n"]));
    }

    [Fact]
    public void NthValue_OutOfRange_ReturnsNull()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");

        var result = engine.Execute(
            "SELECT Val, NTH_VALUE(Val, 3) OVER (ORDER BY Val ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS n FROM T");

        Assert.Null(result.Rows[0]["n"]);
    }

    [Fact]
    public void Lag_IgnoreNulls_SkipsNullValues()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', NULL)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'A', 30)");

        var result = engine.Execute(
            "SELECT Id, LAG(Val) IGNORE NULLS OVER (ORDER BY Id) AS p FROM T");

        Assert.Null(result.Rows[0]["p"]);                        // Id 1: no preceding
        Assert.Equal(10, Convert.ToInt32(result.Rows[1]["p"]));  // Id 2: previous non-null = 10
        Assert.Equal(10, Convert.ToInt32(result.Rows[2]["p"]));  // Id 3: skips NULL → 10
    }

    [Fact]
    public void FirstValue_IgnoreNulls_SkipsLeadingNull()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', NULL)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'A', 30)");

        var result = engine.Execute(
            "SELECT Id, FIRST_VALUE(Val) IGNORE NULLS OVER (ORDER BY Id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS f FROM T");

        foreach (var row in result.Rows)
            Assert.Equal(20, Convert.ToInt32(row["f"]));
    }
}
