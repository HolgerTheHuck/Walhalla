using System;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// B.2.1 — Frame-spec parser and named windows. Frame clauses do not change the output of the
/// implicit-whole-partition ranking functions (ROW_NUMBER/RANK/DENSE_RANK), so these tests verify
/// that the new syntax is accepted, parsed, and produces the correct ranking, plus that named
/// windows resolve identically to inline OVER (...) specifications.
/// </summary>
public class WindowFrameTests
{
    private static WalhallaEngine NewEngine()
    {
        var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Grp STRING, Val INT)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (4, 'B', 5)");
        return engine;
    }

    [Fact]
    public void Frame_RowsBetweenUnboundedPrecedingAndCurrentRow_Parses()
    {
        using var engine = NewEngine();

        var result = engine.Execute(
            "SELECT Val, ROW_NUMBER() OVER (ORDER BY Val ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS rn FROM T ORDER BY Val");

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(1L, result.Rows[0]["rn"]);
        Assert.Equal(2L, result.Rows[1]["rn"]);
        Assert.Equal(3L, result.Rows[2]["rn"]);
        Assert.Equal(4L, result.Rows[3]["rn"]);
    }

    [Fact]
    public void Frame_RowsBetweenPrecedingAndFollowing_Parses()
    {
        using var engine = NewEngine();

        var result = engine.Execute(
            "SELECT Val, ROW_NUMBER() OVER (PARTITION BY Grp ORDER BY Val ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) AS rn FROM T");

        Assert.Equal(4, result.Rows.Count);
    }

    [Fact]
    public void Frame_RangeBetweenUnboundedPrecedingAndUnboundedFollowing_Parses()
    {
        using var engine = NewEngine();

        var result = engine.Execute(
            "SELECT Val, RANK() OVER (ORDER BY Val RANGE BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS rnk FROM T ORDER BY Val");

        // RANK on Val: 5,10,20,20 → 1,2,3,3
        Assert.Equal(1L, result.Rows[0]["rnk"]);
        Assert.Equal(2L, result.Rows[1]["rnk"]);
        Assert.Equal(3L, result.Rows[2]["rnk"]);
        Assert.Equal(3L, result.Rows[3]["rnk"]);
    }

    [Fact]
    public void Frame_GroupsMode_Parses()
    {
        using var engine = NewEngine();

        var result = engine.Execute(
            "SELECT Val, DENSE_RANK() OVER (ORDER BY Val GROUPS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS dr FROM T ORDER BY Val");

        // DENSE_RANK on Val: 5,10,20,20 → 1,2,3,3
        Assert.Equal(1L, result.Rows[0]["dr"]);
        Assert.Equal(2L, result.Rows[1]["dr"]);
        Assert.Equal(3L, result.Rows[2]["dr"]);
        Assert.Equal(3L, result.Rows[3]["dr"]);
    }

    [Fact]
    public void Frame_SingleBoundShortForm_Parses()
    {
        using var engine = NewEngine();

        // "ROWS 5 PRECEDING" is shorthand for BETWEEN 5 PRECEDING AND CURRENT ROW.
        var result = engine.Execute(
            "SELECT Val, ROW_NUMBER() OVER (ORDER BY Val ROWS 5 PRECEDING) AS rn FROM T ORDER BY Val");

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(1L, result.Rows[0]["rn"]);
    }

    [Fact]
    public void Frame_CurrentRowAndUnboundedFollowing_Parses()
    {
        using var engine = NewEngine();

        var result = engine.Execute(
            "SELECT Val, ROW_NUMBER() OVER (ORDER BY Val ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) AS rn FROM T");

        Assert.Equal(4, result.Rows.Count);
    }

    [Fact]
    public void NamedWindow_ResolvesSameAsInline()
    {
        using var engine = NewEngine();

        var result = engine.Execute(
            "SELECT Grp, Val, ROW_NUMBER() OVER w AS rn FROM T WINDOW w AS (PARTITION BY Grp ORDER BY Val)");

        Assert.Equal(4, result.Rows.Count);
        // Partition A ordered by Val: 10,20,20 → 1,2,3 ; partition B: 5 → 1
        // Row order follows storage (Id 1..4): A/10=1, A/20=2, A/20=3, B/5=1
        Assert.Equal(1L, result.Rows[0]["rn"]);
        Assert.Equal(2L, result.Rows[1]["rn"]);
        Assert.Equal(3L, result.Rows[2]["rn"]);
        Assert.Equal(1L, result.Rows[3]["rn"]);
    }

    [Fact]
    public void NamedWindow_WithFrame_Parses()
    {
        using var engine = NewEngine();

        var result = engine.Execute(
            "SELECT Val, RANK() OVER w AS rnk FROM T WINDOW w AS (ORDER BY Val ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) ORDER BY Val");

        Assert.Equal(1L, result.Rows[0]["rnk"]);
        Assert.Equal(2L, result.Rows[1]["rnk"]);
        Assert.Equal(3L, result.Rows[2]["rnk"]);
        Assert.Equal(3L, result.Rows[3]["rnk"]);
    }

    [Fact]
    public void NamedWindow_Undefined_Throws()
    {
        using var engine = NewEngine();

        Assert.ThrowsAny<Exception>(() =>
            engine.Execute("SELECT Val, ROW_NUMBER() OVER missing AS rn FROM T"));
    }

    /// <summary>
    /// B.2.5 regression: rows are inserted in non-ORDER-BY order and the query carries an outer
    /// ORDER BY. This exercises (1) SortPartition keeping each window value bound to its row, and
    /// (2) ApplyPostProcessing mapping precomputed window values back to the correct output row
    /// after the outer sort permutes the rows. The earlier code desynced row↔value here.
    /// </summary>
    [Fact]
    public void WindowValues_UnsortedInput_MapToCorrectRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE U (Id INT PRIMARY KEY, Val INT)");
        // Storage order deliberately differs from ORDER BY order.
        engine.Execute("INSERT INTO U (Id, Val) VALUES (1, 30)");
        engine.Execute("INSERT INTO U (Id, Val) VALUES (2, 10)");
        engine.Execute("INSERT INTO U (Id, Val) VALUES (3, 20)");

        var result = engine.Execute(
            "SELECT Val, ROW_NUMBER() OVER (ORDER BY Val) AS rn FROM U ORDER BY Val");

        Assert.Equal(3, result.Rows.Count);
        // Output ascending by Val: 10/rn=1, 20/rn=2, 30/rn=3.
        Assert.Equal(10, Convert.ToInt32(result.Rows[0]["Val"]));
        Assert.Equal(1L, result.Rows[0]["rn"]);
        Assert.Equal(20, Convert.ToInt32(result.Rows[1]["Val"]));
        Assert.Equal(2L, result.Rows[1]["rn"]);
        Assert.Equal(30, Convert.ToInt32(result.Rows[2]["Val"]));
        Assert.Equal(3L, result.Rows[2]["rn"]);
    }

    /// <summary>
    /// B.2.5 regression: outer ORDER BY descending while the window orders ascending. Verifies the
    /// window value still tracks its own row after the outer sort reverses the row order.
    /// </summary>
    [Fact]
    public void WindowValues_OuterOrderDescending_MapToCorrectRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE U (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO U (Id, Val) VALUES (1, 30)");
        engine.Execute("INSERT INTO U (Id, Val) VALUES (2, 10)");
        engine.Execute("INSERT INTO U (Id, Val) VALUES (3, 20)");

        var result = engine.Execute(
            "SELECT Val, ROW_NUMBER() OVER (ORDER BY Val) AS rn FROM U ORDER BY Val DESC");

        Assert.Equal(3, result.Rows.Count);
        // Output descending by Val: 30/rn=3, 20/rn=2, 10/rn=1.
        Assert.Equal(30, Convert.ToInt32(result.Rows[0]["Val"]));
        Assert.Equal(3L, result.Rows[0]["rn"]);
        Assert.Equal(20, Convert.ToInt32(result.Rows[1]["Val"]));
        Assert.Equal(2L, result.Rows[1]["rn"]);
        Assert.Equal(10, Convert.ToInt32(result.Rows[2]["Val"]));
        Assert.Equal(1L, result.Rows[2]["rn"]);
    }
}
