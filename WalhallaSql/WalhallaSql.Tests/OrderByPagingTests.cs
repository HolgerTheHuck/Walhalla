using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class OrderByPagingTests
{
    // ── ORDER BY ────────────────────────────────────────────────────────────

    [Fact]
    public void OrderBy_SingleColumn_Ascending()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 30)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 20)");

        var result = engine.Execute("SELECT Id, Val FROM T ORDER BY Val");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(10, result.Rows[0]["Val"]);
        Assert.Equal(20, result.Rows[1]["Val"]);
        Assert.Equal(30, result.Rows[2]["Val"]);
    }

    [Fact]
    public void OrderBy_SingleColumn_Descending()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 30)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 20)");

        var result = engine.Execute("SELECT Id, Val FROM T ORDER BY Val DESC");

        Assert.Equal(30, result.Rows[0]["Val"]);
        Assert.Equal(20, result.Rows[1]["Val"]);
        Assert.Equal(10, result.Rows[2]["Val"]);
    }

    [Fact]
    public void OrderBy_WithNulls()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, NULL)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 10)");

        var result = engine.Execute("SELECT Id, Val FROM T ORDER BY Val");

        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void OrderBy_MultiColumn()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A INT, B INT)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, 1, 20)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (2, 1, 10)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (3, 2, 5)");

        var result = engine.Execute("SELECT Id, A, B FROM T ORDER BY A, B");

        Assert.Equal(1, result.Rows[0]["A"]);
        Assert.Equal(10, result.Rows[0]["B"]);
        Assert.Equal(1, result.Rows[1]["A"]);
        Assert.Equal(20, result.Rows[1]["B"]);
        Assert.Equal(2, result.Rows[2]["A"]);
    }

    // ── Paging (LIMIT / OFFSET) ─────────────────────────────────────────────

    [Fact]
    public void Limit_ReturnsRestrictedRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 30)");

        var result = engine.Execute("SELECT Id, Val FROM T LIMIT 2");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Limit_WithOffset()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 30)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (4, 40)");

        var result = engine.Execute("SELECT Id, Val FROM T LIMIT 2 OFFSET 1");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void OffsetFetch_PagingSyntax()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 30)");

        var result = engine.Execute("SELECT Id, Val FROM T OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY");

        Assert.Single(result.Rows);
    }

    // ── DISTINCT ─────────────────────────────────────────────────────────────

    [Fact]
    public void Distinct_SingleColumn_RemovesDuplicates()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'A')");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 'A')");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 'B')");

        var result = engine.Execute("SELECT DISTINCT Val FROM T");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Distinct_AllSameValue_ReturnsSingleRow()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'X')");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 'X')");

        var result = engine.Execute("SELECT DISTINCT Val FROM T");

        Assert.Single(result.Rows);
    }

    // ── LIKE ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Like_PrefixMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Albert')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (3, 'Bob')");

        var result = engine.Execute("SELECT Id, Name FROM T WHERE Name LIKE 'Al%'");

        Assert.Equal(2, result.Rows.Count);
    }

    // ── CROSS JOIN ───────────────────────────────────────────────────────────

    [Fact]
    public void CrossJoin_ProducesCartesianProduct()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, Num INT)");
        engine.Execute("INSERT INTO A (Id, Val) VALUES (1, 'a')");
        engine.Execute("INSERT INTO A (Id, Val) VALUES (2, 'b')");
        engine.Execute("INSERT INTO B (Id, Num) VALUES (1, 10)");
        engine.Execute("INSERT INTO B (Id, Num) VALUES (2, 20)");

        var result = engine.Execute("SELECT a.Val, b.Num FROM A a CROSS JOIN B b");

        Assert.Equal(4, result.Rows.Count);
    }
}
