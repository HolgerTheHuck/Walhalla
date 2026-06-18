using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// Validates the sort-merge join path, which is selected when both inputs are already ordered by
/// their join key (e.g. primary-key = primary-key joins). These tests assert that sort-merge produces
/// results, ordering and NULL handling identical to the hash and nested-loop operators.
/// </summary>
public class SortMergeJoinTests
{
    [Fact]
    public void InnerJoin_KeyEqualsKey_PreSorted_ReturnsMatchesInKeyOrder()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE T1 (Key INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE T2 (Key INT PRIMARY KEY, Data STRING)");

        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (1, 'a1')");
        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (2, 'a2')");
        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (3, 'a3')");

        engine.Execute("INSERT INTO T2 (Key, Data) VALUES (1, 'b1')");
        engine.Execute("INSERT INTO T2 (Key, Data) VALUES (2, 'b2')");
        // No T2 row with Key 3.

        var result = engine.Execute(
            "SELECT t1.Val, t2.Data FROM T1 t1 INNER JOIN T2 t2 ON t1.Key = t2.Key");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("a1", result.Rows[0]["Val"]);
        Assert.Equal("b1", result.Rows[0]["Data"]);
        Assert.Equal("a2", result.Rows[1]["Val"]);
        Assert.Equal("b2", result.Rows[1]["Data"]);
    }

    [Fact]
    public void LeftJoin_KeyEqualsKey_PreSorted_NullFillsUnmatchedLeft()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE T1 (Key INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE T2 (Key INT PRIMARY KEY, Data STRING)");

        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (1, 'a1')");
        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (2, 'a2')");
        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (3, 'a3')");

        engine.Execute("INSERT INTO T2 (Key, Data) VALUES (1, 'b1')");
        engine.Execute("INSERT INTO T2 (Key, Data) VALUES (3, 'b3')");

        var result = engine.Execute(
            "SELECT t1.Val, t2.Data FROM T1 t1 LEFT JOIN T2 t2 ON t1.Key = t2.Key");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("a1", result.Rows[0]["Val"]);
        Assert.Equal("b1", result.Rows[0]["Data"]);
        Assert.Equal("a2", result.Rows[1]["Val"]);
        Assert.Null(result.Rows[1]["Data"]); // Key 2 has no T2 match.
        Assert.Equal("a3", result.Rows[2]["Val"]);
        Assert.Equal("b3", result.Rows[2]["Data"]);
    }

    [Fact]
    public void RightJoin_KeyEqualsKey_PreSorted_NullFillsUnmatchedRight()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE T1 (Key INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE T2 (Key INT PRIMARY KEY, Data STRING)");

        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (1, 'a1')");
        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (3, 'a3')");

        engine.Execute("INSERT INTO T2 (Key, Data) VALUES (1, 'b1')");
        engine.Execute("INSERT INTO T2 (Key, Data) VALUES (2, 'b2')"); // no T1 match
        engine.Execute("INSERT INTO T2 (Key, Data) VALUES (3, 'b3')");

        var result = engine.Execute(
            "SELECT t1.Val, t2.Data FROM T1 t1 RIGHT JOIN T2 t2 ON t1.Key = t2.Key");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("a1", result.Rows[0]["Val"]);
        Assert.Equal("b1", result.Rows[0]["Data"]);
        Assert.Null(result.Rows[1]["Val"]); // Key 2 has no T1 match.
        Assert.Equal("b2", result.Rows[1]["Data"]);
        Assert.Equal("a3", result.Rows[2]["Val"]);
        Assert.Equal("b3", result.Rows[2]["Data"]);
    }

    [Fact]
    public void InnerJoin_KeyEqualsKey_DuplicateKeys_PreSorted_EmitsCartesianPerKey()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Letters (Id INT PRIMARY KEY, K INT, Letter STRING)");
        engine.Execute("CREATE TABLE Numbers (Id INT PRIMARY KEY, K INT, Num INT)");

        // Both inserted in ascending K order so each side is pre-sorted by its join key.
        engine.Execute("INSERT INTO Letters (Id, K, Letter) VALUES (1, 1, 'x')");
        engine.Execute("INSERT INTO Letters (Id, K, Letter) VALUES (2, 1, 'y')");

        engine.Execute("INSERT INTO Numbers (Id, K, Num) VALUES (1, 1, 10)");
        engine.Execute("INSERT INTO Numbers (Id, K, Num) VALUES (2, 1, 20)");

        var result = engine.Execute(
            "SELECT l.Letter, n.Num FROM Letters l INNER JOIN Numbers n ON l.K = n.K");

        // 2 x 2 cartesian for key 1, left-major order: (x,10),(x,20),(y,10),(y,20).
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal("x", result.Rows[0]["Letter"]);
        Assert.Equal(10, result.Rows[0]["Num"]);
        Assert.Equal("x", result.Rows[1]["Letter"]);
        Assert.Equal(20, result.Rows[1]["Num"]);
        Assert.Equal("y", result.Rows[2]["Letter"]);
        Assert.Equal(10, result.Rows[2]["Num"]);
        Assert.Equal("y", result.Rows[3]["Letter"]);
        Assert.Equal(20, result.Rows[3]["Num"]);
    }
}
