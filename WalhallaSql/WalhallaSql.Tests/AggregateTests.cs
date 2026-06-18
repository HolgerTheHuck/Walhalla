using System.Linq;
using Xunit;

namespace WalhallaSql.Tests;

public class AggregateTests
{
    [Fact]
    public void GroupBy_SingleColumn_WithCount()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Sales (Id INT PRIMARY KEY, Region STRING, Amount DOUBLE)");

        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (1, 'EU', 100.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (2, 'EU', 200.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (3, 'US', 150.0)");

        var result = engine.Execute(
            "SELECT Region, COUNT(*) FROM Sales GROUP BY Region");

        Assert.Equal(2, result.Rows.Count);

        var euRow = result.Rows.First(r => (string)r["Region"]! == "EU");
        Assert.Equal(2L, euRow["COUNT(*)"]);
    }

    [Fact]
    public void GroupBy_WithMultipleAggregates()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Sales (Id INT PRIMARY KEY, Region STRING, Amount DOUBLE)");

        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (1, 'EU', 100.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (2, 'EU', 200.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (3, 'US', 150.0)");

        var result = engine.Execute(
            "SELECT Region, COUNT(*), SUM(Amount), AVG(Amount) FROM Sales GROUP BY Region");

        Assert.Equal(2, result.Rows.Count);

        var euRow = result.Rows.First(r => (string)r["Region"]! == "EU");
        Assert.Equal(2L, euRow["COUNT(*)"]);
        Assert.Equal(300.0, euRow["SUM(Amount)"]);
        Assert.Equal(150.0, euRow["AVG(Amount)"]);
    }

    [Fact]
    public void GroupBy_WithMinMax()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Products (Id INT PRIMARY KEY, Category STRING, Price DOUBLE)");

        engine.Execute("INSERT INTO Products (Id, Category, Price) VALUES (1, 'A', 10.0)");
        engine.Execute("INSERT INTO Products (Id, Category, Price) VALUES (2, 'A', 30.0)");
        engine.Execute("INSERT INTO Products (Id, Category, Price) VALUES (3, 'B', 20.0)");

        var result = engine.Execute(
            "SELECT Category, MIN(Price), MAX(Price) FROM Products GROUP BY Category");

        Assert.Equal(2, result.Rows.Count);

        var catARow = result.Rows.First(r => (string)r["Category"]! == "A");
        Assert.Equal(10.0, catARow["MIN(Price)"]);
        Assert.Equal(30.0, catARow["MAX(Price)"]);
    }

    [Fact]
    public void GroupBy_CountColumn_ExcludesNulls()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Data (Id INT PRIMARY KEY, Grp STRING, Val DOUBLE)");

        engine.Execute("INSERT INTO Data (Id, Grp, Val) VALUES (1, 'X', 1.0)");
        engine.Execute("INSERT INTO Data (Id, Grp, Val) VALUES (2, 'X', NULL)");
        engine.Execute("INSERT INTO Data (Id, Grp, Val) VALUES (3, 'X', 3.0)");

        var result = engine.Execute(
            "SELECT Grp, COUNT(Val) FROM Data GROUP BY Grp");

        Assert.Single(result.Rows);
        Assert.Equal(2L, result.Rows[0]["COUNT(Val)"]);
    }

    [Fact]
    public void GroupBy_NoGroups_ReturnsEmpty()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Empty (Id INT PRIMARY KEY, Grp STRING, Val DOUBLE)");

        var result = engine.Execute(
            "SELECT Grp, COUNT(*) FROM Empty GROUP BY Grp");

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void GroupBy_WithWhere_BeforeGrouping()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Sales (Id INT PRIMARY KEY, Region STRING, Amount DOUBLE)");

        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (1, 'EU', 100.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (2, 'EU', 200.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (3, 'US', 150.0)");

        var result = engine.Execute(
            "SELECT Region, SUM(Amount) FROM Sales WHERE Amount > 120 GROUP BY Region");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Having_FiltersGroups()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Sales (Id INT PRIMARY KEY, Region STRING, Amount DOUBLE)");

        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (1, 'EU', 100.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (2, 'EU', 200.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (3, 'US', 50.0)");

        var result = engine.Execute(
            "SELECT Region, SUM(Amount) FROM Sales GROUP BY Region HAVING SUM(Amount) > 150");

        Assert.Single(result.Rows);
        Assert.Equal("EU", result.Rows[0]["Region"]);
    }

    [Fact]
    public void Having_WithMultipleConditions()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Sales (Id INT PRIMARY KEY, Region STRING, Amount DOUBLE)");

        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (1, 'EU', 100.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (2, 'EU', 200.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (3, 'US', 300.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (4, 'US', 400.0)");

        var result = engine.Execute(
            "SELECT Region, SUM(Amount), COUNT(*) FROM Sales GROUP BY Region HAVING SUM(Amount) > 300 AND COUNT(*) >= 2");

        Assert.Single(result.Rows);
        Assert.Equal("US", result.Rows[0]["Region"]);
    }

    [Fact]
    public void GroupBy_PreparedStatement()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Sales (Id INT PRIMARY KEY, Region STRING, Amount DOUBLE)");

        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (1, 'EU', 100.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (2, 'EU', 200.0)");
        engine.Execute("INSERT INTO Sales (Id, Region, Amount) VALUES (3, 'US', 150.0)");

        var stmt = engine.Prepare(
            "SELECT Region, COUNT(*), SUM(Amount) FROM Sales WHERE Amount > @min GROUP BY Region");

        stmt.Bind("@min", 50.0);
        var result = stmt.Execute();

        Assert.Equal(2, result.Rows.Count);
    }
}
