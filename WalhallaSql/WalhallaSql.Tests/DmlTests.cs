using System;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class DmlTests
{
    // ── DROP TABLE ─────────────────────────────────────────────────────────

    [Fact]
    public void DropTable_RemovesTable()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'hello')");

        engine.Execute("DROP TABLE T");

        Assert.Throws<WalhallaException>(() => engine.Execute("SELECT * FROM T"));
    }

    // ── UPDATE all rows ────────────────────────────────────────────────────

    [Fact]
    public void Update_AllRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 30)");

        engine.Execute("UPDATE T SET Val = 0");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Equal(3, result.Rows.Count);
        foreach (var row in result.Rows)
            Assert.Equal(0, row["Val"]);
    }

    [Fact]
    public void Update_MultipleColumnsAllRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A INT, B STRING)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, 10, 'x')");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (2, 20, 'y')");

        engine.Execute("UPDATE T SET A = 99, B = 'z'");

        var result = engine.Execute("SELECT * FROM T");
        foreach (var row in result.Rows)
        {
            Assert.Equal(99, row["A"]);
            Assert.Equal("z", row["B"]);
        }
    }

    // ── DELETE all rows ────────────────────────────────────────────────────

    [Fact]
    public void Delete_AllRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        engine.Execute("DELETE FROM T");

        Assert.Empty(engine.Execute("SELECT * FROM T").Rows);
    }

    [Fact]
    public void Delete_AllRows_TableStillExists()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");

        engine.Execute("DELETE FROM T");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
    }

    // ── INSERT variants ────────────────────────────────────────────────────

    [Fact]
    public void Insert_ExplicitNull()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, NULL)");

        var result = engine.Execute("SELECT * FROM T WHERE Val IS NULL");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Insert_ExplicitNull_IntColumn()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, NULL)");

        var result = engine.Execute("SELECT * FROM T WHERE Val IS NULL");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Insert_FewerColumns()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A STRING, B STRING)");
        engine.Execute("INSERT INTO T (Id, A) VALUES (1, 'hello')");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal("hello", result.Rows[0]["A"]);
    }

    [Fact]
    public void Insert_MultipleRowsExplicitNull()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'a'), (2, NULL), (3, 'c')");

        var result = engine.Execute("SELECT Id FROM T WHERE Val IS NULL");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    // ── Arithmetic in SELECT ───────────────────────────────────────────────

    [Fact]
    public void Select_ArithmeticSubtract()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Revenue DOUBLE, Cost DOUBLE)");
        engine.Execute("INSERT INTO T (Id, Revenue, Cost) VALUES (1, 100.0, 30.0)");

        var result = engine.Execute("SELECT Revenue - Cost AS Profit FROM T");

        Assert.Equal(70.0, result.Rows[0]["Profit"]);
    }

    [Fact]
    public void Select_ArithmeticDivide()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Total DOUBLE, Count INT)");
        engine.Execute("INSERT INTO T (Id, Total, Count) VALUES (1, 100.0, 4)");

        var result = engine.Execute("SELECT Total / Count AS Avg FROM T");

        Assert.Equal(25.0, result.Rows[0]["Avg"]);
    }

    [Fact]
    public void Select_ArithmeticWithAlias()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A INT, B INT, C INT)");
        engine.Execute("INSERT INTO T (Id, A, B, C) VALUES (1, 2, 3, 4)");

        var result = engine.Execute("SELECT A + B * C AS Computed FROM T");

        Assert.Equal(14, Convert.ToInt32(result.Rows[0]["Computed"]));
    }
}
