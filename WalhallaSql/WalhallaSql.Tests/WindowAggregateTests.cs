using System;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// B.2.3 — Frame-aware aggregate window functions: SUM/AVG/COUNT/MIN/MAX OVER (...).
/// Rows are inserted in ORDER BY order so assertions verify the computed value per row
/// independent of the engine's physical row ordering.
/// </summary>
public class WindowAggregateTests
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
    public void Sum_WholePartition_NoOrderBy()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute("SELECT Val, SUM(Val) OVER () AS s FROM T");

        Assert.Equal(4, result.Rows.Count);
        foreach (var row in result.Rows)
            Assert.Equal(100.0, Convert.ToDouble(row["s"]), 5);
    }

    [Fact]
    public void Sum_RunningWithOrderBy_DefaultFrame()
    {
        using var engine = NewEngine();
        Seed(engine);

        // ORDER BY without explicit frame → RANGE UNBOUNDED PRECEDING .. CURRENT ROW (running sum).
        var result = engine.Execute("SELECT Val, SUM(Val) OVER (ORDER BY Val) AS s FROM T");

        Assert.Equal(10.0, Convert.ToDouble(result.Rows[0]["s"]), 5);
        Assert.Equal(30.0, Convert.ToDouble(result.Rows[1]["s"]), 5);
        Assert.Equal(60.0, Convert.ToDouble(result.Rows[2]["s"]), 5);
        Assert.Equal(100.0, Convert.ToDouble(result.Rows[3]["s"]), 5);
    }

    [Fact]
    public void Avg_WholePartition()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute("SELECT Val, AVG(Val) OVER () AS a FROM T");

        foreach (var row in result.Rows)
            Assert.Equal(25.0, Convert.ToDouble(row["a"]), 5);
    }

    [Fact]
    public void Count_RunningWithOrderBy()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute("SELECT Val, COUNT(*) OVER (ORDER BY Val) AS c FROM T");

        Assert.Equal(1L, result.Rows[0]["c"]);
        Assert.Equal(2L, result.Rows[1]["c"]);
        Assert.Equal(3L, result.Rows[2]["c"]);
        Assert.Equal(4L, result.Rows[3]["c"]);
    }

    [Fact]
    public void Min_Max_WholePartition()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute(
            "SELECT Val, MIN(Val) OVER () AS mn, MAX(Val) OVER () AS mx FROM T");

        foreach (var row in result.Rows)
        {
            Assert.Equal(10, Convert.ToInt32(row["mn"]));
            Assert.Equal(40, Convert.ToInt32(row["mx"]));
        }
    }

    [Fact]
    public void Sum_RowsBetweenPrecedingAndCurrentRow()
    {
        using var engine = NewEngine();
        Seed(engine);

        // Sliding window of up to 2 rows ending at the current row.
        var result = engine.Execute(
            "SELECT Val, SUM(Val) OVER (ORDER BY Val ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS s FROM T");

        Assert.Equal(10.0, Convert.ToDouble(result.Rows[0]["s"]), 5); // [10]
        Assert.Equal(30.0, Convert.ToDouble(result.Rows[1]["s"]), 5); // [10,20]
        Assert.Equal(50.0, Convert.ToDouble(result.Rows[2]["s"]), 5); // [20,30]
        Assert.Equal(70.0, Convert.ToDouble(result.Rows[3]["s"]), 5); // [30,40]
    }

    [Fact]
    public void Sum_RowsBetweenCurrentRowAndFollowing()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute(
            "SELECT Val, SUM(Val) OVER (ORDER BY Val ROWS BETWEEN CURRENT ROW AND 1 FOLLOWING) AS s FROM T");

        Assert.Equal(30.0, Convert.ToDouble(result.Rows[0]["s"]), 5); // [10,20]
        Assert.Equal(50.0, Convert.ToDouble(result.Rows[1]["s"]), 5); // [20,30]
        Assert.Equal(70.0, Convert.ToDouble(result.Rows[2]["s"]), 5); // [30,40]
        Assert.Equal(40.0, Convert.ToDouble(result.Rows[3]["s"]), 5); // [40]
    }

    [Fact]
    public void Sum_RowsUnboundedFollowing()
    {
        using var engine = NewEngine();
        Seed(engine);

        var result = engine.Execute(
            "SELECT Val, SUM(Val) OVER (ORDER BY Val ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) AS s FROM T");

        Assert.Equal(100.0, Convert.ToDouble(result.Rows[0]["s"]), 5);
        Assert.Equal(90.0, Convert.ToDouble(result.Rows[1]["s"]), 5);
        Assert.Equal(70.0, Convert.ToDouble(result.Rows[2]["s"]), 5);
        Assert.Equal(40.0, Convert.ToDouble(result.Rows[3]["s"]), 5);
    }

    [Fact]
    public void Sum_WithPartition()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'B', 100)");

        var result = engine.Execute(
            "SELECT Grp, Val, SUM(Val) OVER (PARTITION BY Grp) AS s FROM T");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(30.0, Convert.ToDouble(result.Rows[0]["s"]), 5);
        Assert.Equal(30.0, Convert.ToDouble(result.Rows[1]["s"]), 5);
        Assert.Equal(100.0, Convert.ToDouble(result.Rows[2]["s"]), 5);
    }

    [Fact]
    public void Sum_RangeCurrentRow_PeerAware()
    {
        using var engine = NewEngine();
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (4, 'A', 30)");

        // RANGE running sum: peers (Val=20) share the same cumulative value.
        var result = engine.Execute(
            "SELECT Val, SUM(Val) OVER (ORDER BY Val RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM T");

        Assert.Equal(10.0, Convert.ToDouble(result.Rows[0]["s"]), 5); // 10
        Assert.Equal(50.0, Convert.ToDouble(result.Rows[1]["s"]), 5); // 10+20+20
        Assert.Equal(50.0, Convert.ToDouble(result.Rows[2]["s"]), 5); // peer
        Assert.Equal(80.0, Convert.ToDouble(result.Rows[3]["s"]), 5); // +30
    }

    [Fact]
    public void Count_EmptyFrame_ReturnsZero()
    {
        using var engine = NewEngine();
        Seed(engine);

        // First row's frame "1 PRECEDING .. 1 PRECEDING" is empty for the first row.
        var result = engine.Execute(
            "SELECT Val, COUNT(*) OVER (ORDER BY Val ROWS BETWEEN 1 PRECEDING AND 1 PRECEDING) AS c FROM T");

        Assert.Equal(0L, result.Rows[0]["c"]);
        Assert.Equal(1L, result.Rows[1]["c"]);
    }
}
