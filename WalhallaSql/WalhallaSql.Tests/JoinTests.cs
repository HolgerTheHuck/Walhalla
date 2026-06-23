using System;
using System.IO;
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

    [Fact]
    public void InnerJoin_CrossNumericTypes_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id BIGINT PRIMARY KEY, Data STRING)");

        engine.Execute("INSERT INTO A (Id, Val) VALUES (1, 'a1')");
        engine.Execute("INSERT INTO B (Id, Data) VALUES (1, 'b1')");

        var result = engine.Execute(
            "SELECT a.Val, b.Data FROM A a INNER JOIN B b ON a.Id = b.Id");

        Assert.Single(result.Rows);
        Assert.Equal("a1", result.Rows[0]["Val"]);
        Assert.Equal("b1", result.Rows[0]["Data"]);
    }

    [Fact]
    public void InnerJoin_ThreeTables_CrossNumericKeys_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        // Simuliert eine typische Migration aus MSSQL: PK- und FK-Spalten können
        // unterschiedliche numerische Typen bekommen (z. B. INT vs BIGINT).
        engine.Execute("CREATE TABLE Module (Id INT PRIMARY KEY, DetailsId BIGINT, NameResource INT)");
        engine.Execute("CREATE TABLE ModuleDetails (Id BIGINT PRIMARY KEY, Name STRING, NameResource INT)");
        engine.Execute("CREATE TABLE Documents (Id INT PRIMARY KEY, Content STRING)");

        engine.Execute("INSERT INTO Module (Id, DetailsId, NameResource) VALUES (1, 100, 200)");
        engine.Execute("INSERT INTO Module (Id, DetailsId, NameResource) VALUES (2, 101, 201)");

        engine.Execute("INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (100, 'Details-A', 200)");
        engine.Execute("INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (101, 'Details-B', 201)");

        engine.Execute("INSERT INTO Documents (Id, Content) VALUES (200, 'Content-A')");
        engine.Execute("INSERT INTO Documents (Id, Content) VALUES (201, 'Content-B')");

        var result = engine.Execute(
            "SELECT m.Id, md.Name, d.Content FROM Module m " +
            "JOIN ModuleDetails md ON md.Id = m.DetailsId " +
            "JOIN Documents d ON d.Id = md.NameResource");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void InnerJoin_ThreeTables_SecondJoinRefersToBaseTable_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");
        engine.Execute("CREATE TABLE Tags (Id INT PRIMARY KEY, Name STRING, CustomerId INT)");

        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (10, 1, 100.0)");
        engine.Execute("INSERT INTO Tags (Id, Name, CustomerId) VALUES (20, 'VIP', 1)");

        var result = engine.Execute(
            "SELECT c.Name, o.Amount, t.Name FROM Customers c " +
            "INNER JOIN Orders o ON o.CustomerId = c.Id " +
            "INNER JOIN Tags t ON t.CustomerId = c.Id");

        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0]["Name"]);
        Assert.Equal(100.0, result.Rows[0]["Amount"]);
    }

    [Fact]
    public void InnerJoin_GuidKeys_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE ModuleDetails (Id BIGINT PRIMARY KEY, Name STRING, NameResource GUID)");
        engine.Execute("CREATE TABLE Documents (Id GUID PRIMARY KEY, Content STRING)");

        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();

        engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (1, 'Details-A', '{g1}')");
        engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (2, 'Details-B', '{g2}')");

        engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g1}', 'Content-A')");
        engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g2}', 'Content-B')");

        var result = engine.Execute(
            "SELECT md.Name, d.Content FROM ModuleDetails md " +
            "JOIN Documents d ON d.Id = md.NameResource");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void InnerJoin_GuidAndStringKeys_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE ModuleDetails (Id BIGINT PRIMARY KEY, Name STRING, NameResource STRING)");
        engine.Execute("CREATE TABLE Documents (Id GUID PRIMARY KEY, Content STRING)");

        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();

        engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (1, 'Details-A', '{g1}')");
        engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (2, 'Details-B', '{g2}')");

        engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g1}', 'Content-A')");
        engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g2}', 'Content-B')");

        var result = engine.Execute(
            "SELECT md.Name, d.Content FROM ModuleDetails md " +
            "JOIN Documents d ON d.Id = md.NameResource");

        Assert.Equal(2, result.Rows.Count);
    }



    [Fact]
    public void InnerJoin_GuidAndStringKeys_HashJoin_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE ModuleDetails (Id BIGINT PRIMARY KEY, Name STRING, NameResource STRING)");
        engine.Execute("CREATE TABLE Documents (Id GUID PRIMARY KEY, Content STRING)");

        var guids = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToArray();

        for (int i = 0; i < guids.Length; i++)
        {
            engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES ({1000 + i}, 'Details-{i}', '{guids[i]}')");
            engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{guids[i]}', 'Content-{i}')");
        }

        var result = engine.Execute(
            "SELECT md.Name, d.Content FROM ModuleDetails md " +
            "JOIN Documents d ON d.Id = md.NameResource");

        Assert.Equal(150, result.Rows.Count);
    }

    [Fact]
    public void InnerJoin_ThreeTables_GuidString_HashJoin_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Module (Id BIGINT PRIMARY KEY, Details_ID BIGINT)");
        engine.Execute("CREATE TABLE ModuleDetails (Id BIGINT PRIMARY KEY, Name STRING, NameResource STRING)");
        engine.Execute("CREATE TABLE Documents (Id GUID PRIMARY KEY, Content STRING)");

        var guids = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToArray();

        for (int i = 0; i < guids.Length; i++)
        {
            engine.Execute($"INSERT INTO Module (Id, Details_ID) VALUES ({i}, {1000 + i})");
            engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES ({1000 + i}, 'Details-{i}', '{guids[i]}')");
            engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{guids[i]}', 'Content-{i}')");
        }

        var result = engine.Execute(
            "SELECT m.Id, md.Name, d.Content FROM Module m " +
            "JOIN ModuleDetails md ON md.Id = m.Details_ID " +
            "JOIN Documents d ON d.Id = md.NameResource");

        Assert.Equal(150, result.Rows.Count);
    }

    [Fact]
    public void InnerJoin_GuidKeys_DiskMode_ReturnsMatchingRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"walhalla-guid-join-test-{Guid.NewGuid()}");
        try
        {
            using var engine = WalhallaEngine.Open(path);

            engine.Execute("CREATE TABLE ModuleDetails (Id BIGINT PRIMARY KEY, Name STRING, NameResource GUID)");
            engine.Execute("CREATE TABLE Documents (Id GUID PRIMARY KEY, Content STRING)");

            var g1 = Guid.NewGuid();
            var g2 = Guid.NewGuid();

            engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (1, 'Details-A', '{g1}')");
            engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (2, 'Details-B', '{g2}')");

            engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g1}', 'Content-A')");
            engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g2}', 'Content-B')");

            engine.Checkpoint();

            var result = engine.Execute(
                "SELECT md.Name, d.Content FROM ModuleDetails md " +
                "JOIN Documents d ON d.Id = md.NameResource");

            Assert.Equal(2, result.Rows.Count);
        }
        finally
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InnerJoin_ThreeTables_MixedCaseNames_ReturnsMatchingRows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Module (ID INT PRIMARY KEY, Details_ID BIGINT, NameResource GUID)");
        engine.Execute("CREATE TABLE ModuleDetails (ID BIGINT PRIMARY KEY, Name STRING, NameResource GUID)");
        engine.Execute("CREATE TABLE Documents (Id GUID PRIMARY KEY, Content STRING)");

        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();

        engine.Execute($"INSERT INTO Module (ID, Details_ID, NameResource) VALUES (1, 100, '{g1}')");
        engine.Execute($"INSERT INTO Module (ID, Details_ID, NameResource) VALUES (2, 101, '{g2}')");

        engine.Execute($"INSERT INTO ModuleDetails (ID, Name, NameResource) VALUES (100, 'Details-A', '{g1}')");
        engine.Execute($"INSERT INTO ModuleDetails (ID, Name, NameResource) VALUES (101, 'Details-B', '{g2}')");

        engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g1}', 'Content-A')");
        engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g2}', 'Content-B')");

        var result = engine.Execute(
            "SELECT m.ID, md.Name, nameDoc.Content FROM Module m " +
            "JOIN ModuleDetails md ON md.ID = m.Details_ID " +
            "JOIN Documents nameDoc ON nameDoc.Id = md.NameResource");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void InnerJoin_GuidAndStringKeys_DiskMode_ReturnsMatchingRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"walhalla-guid-string-join-test-{Guid.NewGuid()}");
        try
        {
            using var engine = WalhallaEngine.Open(path);

            // Simuliert eine Migration, bei der ein uniqueidentifier einmal als GUID
            // und einmal als STRING landet (z. B. wegen unterschiedlicher Heuristiken).
            engine.Execute("CREATE TABLE ModuleDetails (Id BIGINT PRIMARY KEY, Name STRING, NameResource STRING)");
            engine.Execute("CREATE TABLE Documents (Id GUID PRIMARY KEY, Content STRING)");

            var g1 = Guid.NewGuid();
            var g2 = Guid.NewGuid();

            engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (1, 'Details-A', '{g1}')");
            engine.Execute($"INSERT INTO ModuleDetails (Id, Name, NameResource) VALUES (2, 'Details-B', '{g2}')");

            engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g1}', 'Content-A')");
            engine.Execute($"INSERT INTO Documents (Id, Content) VALUES ('{g2}', 'Content-B')");

            engine.Checkpoint();

            var result = engine.Execute(
                "SELECT md.Name, d.Content FROM ModuleDetails md " +
                "JOIN Documents d ON d.Id = md.NameResource");

            Assert.Equal(2, result.Rows.Count);
        }
        finally
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
