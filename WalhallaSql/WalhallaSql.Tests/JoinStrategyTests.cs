using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// Validates the size-based join-strategy selection. The existing <see cref="JoinTests"/> cover the
/// nested-loop path (small tables); these tests force the hash path by using a build side larger than
/// <c>JoinStrategySelector.NestedLoopMaxBuildRows</c> (100) and assert identical, correct results.
/// </summary>
public class JoinStrategyTests
{
    private const int LargeRowCount = 150; // > 100 → forces the hash join path

    [Fact]
    public void InnerJoin_LargeRightTable_UsesHash_ReturnsAllMatches()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");

        for (int id = 1; id <= 5; id++)
            engine.Execute($"INSERT INTO Customers (Id, Name) VALUES ({id}, 'C{id}')");

        // 150 orders spread across the 5 customers → 30 orders per customer.
        for (int i = 0; i < LargeRowCount; i++)
        {
            int customerId = (i % 5) + 1;
            engine.Execute(
                $"INSERT INTO Orders (Id, CustomerId, Amount) VALUES ({i + 1}, {customerId}, {i})");
        }

        var result = engine.Execute(
            "SELECT c.Name, o.Amount FROM Customers c INNER JOIN Orders o ON c.Id = o.CustomerId");

        Assert.Equal(LargeRowCount, result.Rows.Count);
    }

    [Fact]
    public void LeftJoin_LargeRightTable_UsesHash_KeepsUnmatchedLeftRow()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");

        for (int id = 1; id <= 5; id++)
            engine.Execute($"INSERT INTO Customers (Id, Name) VALUES ({id}, 'C{id}')");
        // Customer 999 has no orders.
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (999, 'Lonely')");

        for (int i = 0; i < LargeRowCount; i++)
        {
            int customerId = (i % 5) + 1;
            engine.Execute(
                $"INSERT INTO Orders (Id, CustomerId, Amount) VALUES ({i + 1}, {customerId}, {i})");
        }

        var result = engine.Execute(
            "SELECT c.Name, o.Amount FROM Customers c LEFT JOIN Orders o ON c.Id = o.CustomerId");

        // 150 matched rows + 1 null-filled row for the unmatched customer.
        Assert.Equal(LargeRowCount + 1, result.Rows.Count);
        Assert.Contains(result.Rows, r => Equals(r["Name"], "Lonely") && r["Amount"] == null);
    }

    [Fact]
    public void RightJoin_LargeLeftTable_UsesHash_NullFillsUnmatchedRight()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");

        // Large left side (build side for RIGHT join) → forces hash.
        for (int id = 1; id <= LargeRowCount; id++)
            engine.Execute($"INSERT INTO Customers (Id, Name) VALUES ({id}, 'C{id}')");

        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 10.0)");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 2, 20.0)");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (3, 999, 30.0)"); // no match

        var result = engine.Execute(
            "SELECT c.Name, o.Amount FROM Customers c RIGHT JOIN Orders o ON c.Id = o.CustomerId");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("C1", result.Rows[0]["Name"]);
        Assert.Equal("C2", result.Rows[1]["Name"]);
        Assert.Null(result.Rows[2]["Name"]);
        Assert.Equal(30.0, result.Rows[2]["Amount"]);
    }
}
