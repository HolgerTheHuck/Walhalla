using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WalhallaSql.Execution;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class DiagnosticCteJoinTests
{
    [Fact]
    public void NonRecursiveCte_JoinWithCte_Works()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Emp (Id INT PRIMARY KEY, Name STRING, ManagerId INT)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (1, 'Alice', NULL)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (2, 'Bob', 1)");

        // Non-recursive CTE used in a JOIN
        // Emp: (1,Alice,NULL), (2,Bob,1).  CTE: only Alice (ManagerId IS NULL) = (1,Alice).
        // JOIN ON e.ManagerId = cte.Id → only Bob (ManagerId=1) matches cte.Id=1.
        var result = engine.Execute(@"
            WITH cte AS (SELECT Id, Name FROM Emp WHERE ManagerId IS NULL)
            SELECT e.Id, e.Name FROM Emp e JOIN cte ON e.ManagerId = cte.Id");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0].GetValue(0));
    }

    [Fact]
    public void RecursiveCte_NoJoin_Works()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (N INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (N) VALUES (1)");

        var result = engine.Execute(@"
            WITH RECURSIVE cte AS (
                SELECT N FROM T
                UNION ALL
                SELECT N+1 FROM cte WHERE N < 3
            )
            SELECT * FROM cte");
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void RecursiveCte_Join_CteOnRight()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Emp (Id INT PRIMARY KEY, Name STRING, ManagerId INT)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (1, 'Alice', NULL)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (2, 'Bob', 1)");

        var result = engine.Execute(@"
            WITH RECURSIVE cte AS (
                SELECT Id, Name FROM Emp WHERE ManagerId IS NULL
                UNION ALL
                SELECT e.Id, e.Name FROM Emp e JOIN cte ON e.ManagerId = cte.Id
            )
            SELECT * FROM cte");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Diagnostic_VerifyTempTableHasRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Emp (Id INT PRIMARY KEY, Name STRING, ManagerId INT)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (1, 'Alice', NULL)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (2, 'Bob', 1)");

        // Execute CTE without JOIN — verify it produces rows
        var result1 = engine.Execute(@"
            WITH cte AS (SELECT Id, Name FROM Emp WHERE ManagerId IS NULL)
            SELECT * FROM cte");
        Assert.Single(result1.Rows);
        Assert.Equal(1, result1.Rows[0].GetValue(0));

        // Now execute with JOIN — only Bob (ManagerId=1) matches cte.Id=1
        var result2 = engine.Execute(@"
            WITH cte AS (SELECT Id, Name FROM Emp WHERE ManagerId IS NULL)
            SELECT e.Id, e.Name FROM Emp e JOIN cte ON e.ManagerId = cte.Id");
        Assert.Single(result2.Rows);
        Assert.Equal(2, result2.Rows[0].GetValue(0));
    }

    [Fact]
    public void Diagnostic_DirectJoinWithoutCte_Works()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Emp (Id INT PRIMARY KEY, Name STRING, ManagerId INT)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (1, 'Alice', NULL)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (2, 'Bob', 1)");

        // Self-join: this tests that the JOIN infrastructure works for real tables
        var result = engine.Execute(@"
            SELECT e.Id, e.Name FROM Emp e JOIN Emp m ON e.ManagerId = m.Id");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Diagnostic_ManualTempTable_ScanWorks()
    {
        // Replicate what ExecuteNonRecursiveWith does manually through public API
        using var engine = WalhallaEngine.InMemory();

        // Create a real table with data
        engine.Execute("CREATE TABLE Emp (Id INT PRIMARY KEY, Name STRING, ManagerId INT)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (1, 'Alice', NULL)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (2, 'Bob', 1)");

        // Create a permanent table to simulate the CTE temp table
        engine.Execute("CREATE TABLE MockCte (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO MockCte (Id, Name) VALUES (1, 'Alice')");

        // Now JOIN against the mock CTE
        var result = engine.Execute(@"
            SELECT e.Id, e.Name FROM Emp e JOIN MockCte ON e.ManagerId = MockCte.Id");
        Assert.Single(result.Rows);
    }
}
