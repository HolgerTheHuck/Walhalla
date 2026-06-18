using System;
using System.Linq;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class JoinTests
{
    [Fact]
    public void InnerJoin_TwoTables_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");

        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (3, 'Charlie')");

        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100.5)");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 1, 200.0)");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (3, 2, 50.0)");

        var result = engine.Execute(
            "SELECT c.Name, o.Amount FROM Customers c INNER JOIN Orders o ON c.Id = o.CustomerId");

        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void InnerJoin_NoMatches_ReturnsEmpty()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, AId INT, Data STRING)");

        engine.Execute("INSERT INTO A (Id, Val) VALUES (1, 'x')");
        engine.Execute("INSERT INTO B (Id, AId, Data) VALUES (1, 999, 'y')");

        var result = engine.Execute(
            "SELECT a.Val, b.Data FROM A a INNER JOIN B b ON a.Id = b.AId");

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void SelfJoin_SameTable_WithAliases()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Employees (Id INT PRIMARY KEY, Name STRING, ManagerId INT)");

        engine.Execute("INSERT INTO Employees (Id, Name, ManagerId) VALUES (1, 'Boss', NULL)");
        engine.Execute("INSERT INTO Employees (Id, Name, ManagerId) VALUES (2, 'Worker', 1)");

        var result = engine.Execute(
            "SELECT e.Name, m.Name FROM Employees e INNER JOIN Employees m ON e.ManagerId = m.Id");

        Assert.Single(result.Rows);
    }

    [Fact]
    public void LeftJoin_ReturnsUnmatchedLeftRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");

        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");

        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100.0)");

        var result = engine.Execute(
            "SELECT c.Name, o.Amount FROM Customers c LEFT JOIN Orders o ON c.Id = o.CustomerId");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Join_WithWhereClause_FiltersCorrectly()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING, Region STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");

        engine.Execute("INSERT INTO Customers (Id, Name, Region) VALUES (1, 'Alice', 'EU')");
        engine.Execute("INSERT INTO Customers (Id, Name, Region) VALUES (2, 'Bob', 'US')");

        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100.0)");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 2, 200.0)");

        var result = engine.Execute(
            "SELECT c.Name, o.Amount FROM Customers c INNER JOIN Orders o ON c.Id = o.CustomerId WHERE c.Region = 'EU'");

        Assert.Single(result.Rows);
    }

    [Fact]
    public void Join_SelectStar_ReturnsAllColumns()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, AId INT, Data STRING)");

        engine.Execute("INSERT INTO A (Id, Val) VALUES (1, 'hello')");
        engine.Execute("INSERT INTO B (Id, AId, Data) VALUES (1, 1, 'world')");

        var result = engine.Execute(
            "SELECT * FROM A a INNER JOIN B b ON a.Id = b.AId");

        Assert.Single(result.Rows);
    }

    [Fact]
    public void Join_UsingClause_ExpandsToOnPredicate()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");

        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100.0)");

        var result = engine.Execute(
            "SELECT c.Name, o.Amount FROM Customers c INNER JOIN Orders o USING (Id)");

        Assert.Single(result.Rows);
    }

    [Fact]
    public void Join_Using_WithExplicitSharedColumn()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE T1 (Key INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE T2 (Key INT PRIMARY KEY, Data STRING)");

        engine.Execute("INSERT INTO T1 (Key, Val) VALUES (1, 'hello')");
        engine.Execute("INSERT INTO T2 (Key, Data) VALUES (1, 'world')");

        var result = engine.Execute(
            "SELECT t1.Val, t2.Data FROM T1 t1 INNER JOIN T2 t2 USING (Key)");

        Assert.Single(result.Rows);
        Assert.Equal("hello", result.Rows[0]["Val"]);
        Assert.Equal("world", result.Rows[0]["Data"]);
    }

    [Fact]
    public void RightJoin_ReturnsAllRightRows_WithNullLeftOnNoMatch()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");

        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100.0)");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 99, 200.0)");

        // Order 2 has CustomerId=99 which doesn't match any customer
        var result = engine.Execute(
            "SELECT c.Name, o.Amount FROM Customers c RIGHT JOIN Orders o ON c.Id = o.CustomerId");

        Assert.Equal(2, result.Rows.Count);
        // First row: matching customer
        Assert.Equal("Alice", result.Rows[0]["Name"]);
        // Second row: no matching customer, Name should be null
        Assert.Null(result.Rows[1]["Name"]);
        Assert.Equal(200.0, result.Rows[1]["Amount"]);
    }

    [Fact]
    public void RightJoin_AllMatch_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, AId INT, Data STRING)");

        engine.Execute("INSERT INTO A (Id, Val) VALUES (1, 'a1')");
        engine.Execute("INSERT INTO A (Id, Val) VALUES (2, 'a2')");
        engine.Execute("INSERT INTO B (Id, AId, Data) VALUES (1, 1, 'b1')");
        engine.Execute("INSERT INTO B (Id, AId, Data) VALUES (2, 2, 'b2')");

        var result = engine.Execute(
            "SELECT a.Val, b.Data FROM A a RIGHT JOIN B b ON a.Id = b.AId");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("a1", result.Rows[0]["Val"]);
        Assert.Equal("b1", result.Rows[0]["Data"]);
    }
}
