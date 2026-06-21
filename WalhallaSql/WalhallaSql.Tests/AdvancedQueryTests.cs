using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class AdvancedQueryTests
{
    // ── INSERT ... SELECT ───────────────────────────────────────────────────

    [Fact]
    public void InsertSelect_CopiesRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Src (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE Dst (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO Src (Id, Val) VALUES (1, 'a')");
        engine.Execute("INSERT INTO Src (Id, Val) VALUES (2, 'b')");

        engine.Execute("INSERT INTO Dst (Id, Val) SELECT Id, Val FROM Src");

        Assert.Equal(2, engine.Execute("SELECT * FROM Dst").Rows.Count);
    }

    [Fact]
    public void InsertSelect_WithWhere()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Src (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("CREATE TABLE Dst (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO Src (Id, Val) VALUES (1, 5)");
        engine.Execute("INSERT INTO Src (Id, Val) VALUES (2, 15)");
        engine.Execute("INSERT INTO Src (Id, Val) VALUES (3, 25)");

        engine.Execute("INSERT INTO Dst (Id, Val) SELECT Id, Val FROM Src WHERE Val > 10");

        Assert.Equal(2, engine.Execute("SELECT * FROM Dst").Rows.Count);
    }

    // ── Derived Tables ──────────────────────────────────────────────────────

    [Fact]
    public void DerivedTable_FromSubquery()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 30)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 20)");

        var result = engine.Execute(
            "SELECT * FROM (SELECT Id, Val FROM T WHERE Val > 15) AS sub");

        Assert.Equal(2, result.Rows.Count);
    }

    // ── CTEs (WITH) ─────────────────────────────────────────────────────────

    [Fact]
    public void WithClause_SimpleCte()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        var result = engine.Execute(
            "WITH cte AS (SELECT Id, Val FROM T WHERE Val > 5) SELECT * FROM cte");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void WithClause_MultipleCtes()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 30)");

        var result = engine.Execute(@"
            WITH
                cte1 AS (SELECT Id, Val FROM T WHERE Val >= 20),
                cte2 AS (SELECT Id, Val FROM cte1 WHERE Val <= 30)
            SELECT * FROM cte2");

        Assert.Equal(2, result.Rows.Count);
    }

    // ── UNION / UNION ALL / EXCEPT / INTERSECT ──────────────────────────────

    [Fact]
    public void Union_CombinesRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO A (Id, Val) VALUES (1, 'a1')");
        engine.Execute("INSERT INTO B (Id, Val) VALUES (2, 'b1')");

        var result = engine.Execute(
            "SELECT Val FROM A UNION SELECT Val FROM B");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Union_DeduplicatesRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO A (Id, Val) VALUES (1, 'same')");
        engine.Execute("INSERT INTO B (Id, Val) VALUES (2, 'same')");

        var result = engine.Execute(
            "SELECT Val FROM A UNION SELECT Val FROM B");

        Assert.Single(result.Rows);
    }

    [Fact]
    public void UnionAll_KeepsDuplicates()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO A (Id, Val) VALUES (1, 'dup')");
        engine.Execute("INSERT INTO B (Id, Val) VALUES (2, 'dup')");

        var result = engine.Execute(
            "SELECT Val FROM A UNION ALL SELECT Val FROM B");

        Assert.Equal(2, result.Rows.Count);
    }

    // ── Window Functions ────────────────────────────────────────────────────

    [Fact]
    public void Window_RowNumber_NoPartition()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 30)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 20)");

        var result = engine.Execute(
            "SELECT Val, ROW_NUMBER() OVER (ORDER BY Val) AS rn FROM T ORDER BY Val");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(1L, result.Rows[0]["rn"]);
        Assert.Equal(2L, result.Rows[1]["rn"]);
        Assert.Equal(3L, result.Rows[2]["rn"]);
    }

    [Fact]
    public void Window_RowNumber_WithPartitionBy()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Grp STRING, Val INT)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (1, 'A', 10)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (2, 'A', 20)");
        engine.Execute("INSERT INTO T (Id, Grp, Val) VALUES (3, 'B', 5)");

        var result = engine.Execute(
            "SELECT Grp, Val, ROW_NUMBER() OVER (PARTITION BY Grp ORDER BY Val) AS rn FROM T");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(1L, result.Rows[0]["rn"]);
        Assert.Equal(2L, result.Rows[1]["rn"]);
        Assert.Equal(1L, result.Rows[2]["rn"]);
    }

    [Fact]
    public void Window_Rank()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 20)");

        var result = engine.Execute(
            "SELECT Val, RANK() OVER (ORDER BY Val) AS rnk FROM T");

        Assert.Equal(1L, result.Rows[0]["rnk"]);
        Assert.Equal(1L, result.Rows[1]["rnk"]);
        Assert.Equal(3L, result.Rows[2]["rnk"]);
    }

    // ── Correlated Subquery Rewriting (EXISTS → JOIN) ──────────────────────

    [Fact]
    public void Exists_Correlated_RewrittenAsJoin()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100.0)");

        var result = engine.Execute(@"
            SELECT c.Name FROM Customers c
            WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.CustomerId = c.Id)");

        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0]["Name"]);
    }

    // ── COALESCE ─────────────────────────────────────────────────────────

    [Fact]
    public void Coalesce_InSelect_ReturnsFirstNonNull()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A STRING, B STRING)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, NULL, 'fallback')");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (2, 'primary', 'ignored')");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (3, NULL, NULL)");

        var result = engine.Execute("SELECT COALESCE(A, B) AS Val FROM T");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("fallback", result.Rows[0]["Val"]);
        Assert.Equal("primary", result.Rows[1]["Val"]);
        Assert.Null(result.Rows[2]["Val"]);
    }

    [Fact]
    public void Coalesce_InWhere_WorksAsFilter()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A STRING, B STRING)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, NULL, 'fallback')");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (2, 'primary', 'ignored')");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (3, NULL, NULL)");

        var result = engine.Execute("SELECT Id FROM T WHERE COALESCE(A, B) IS NOT NULL");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Coalesce_ThreeArgs_ReturnsFirstNonNull()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A STRING, B STRING, C STRING)");
        engine.Execute("INSERT INTO T (Id, A, B, C) VALUES (1, NULL, NULL, 'third')");

        var result = engine.Execute("SELECT COALESCE(A, B, C) AS Val FROM T");

        Assert.Equal("third", result.Rows[0]["Val"]);
    }

    [Fact]
    public void Coalesce_WithLiterals()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, NULL)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Alice')");

        var result = engine.Execute("SELECT COALESCE(Name, 'Unknown') AS Display FROM T");

        Assert.Equal("Unknown", result.Rows[0]["Display"]);
        Assert.Equal("Alice", result.Rows[1]["Display"]);
    }

    // ── EXPLAIN ───────────────────────────────────────────────────────────

    [Fact]
    public void Explain_Select_ReturnsQueryPlan()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Val INT)");
        engine.Execute("INSERT INTO T (Id, Name, Val) VALUES (1, 'Alice', 10)");

        var result = engine.Execute("EXPLAIN SELECT Id, Name FROM T WHERE Val > 5 ORDER BY Name");

        Assert.NotEmpty(result.Rows);
        Assert.Contains("Operation", result.ColumnNames);
        Assert.Contains("Target", result.ColumnNames);
        Assert.Contains("Details", result.ColumnNames);

        // Should have SCAN, FILTER, SORT, and PROJECT operations
        var ops = result.Rows.Select(r => r["Operation"] as string).ToList();
        Assert.Contains("SCAN", ops);
        Assert.Contains("PROJECT", ops);
    }

    [Fact]
    public void Explain_Join_ShowsJoinOperations()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, AId INT, Data STRING)");

        var result = engine.Execute(
            "EXPLAIN SELECT a.Val, b.Data FROM A a INNER JOIN B b ON a.Id = b.AId");

        var ops = result.Rows.Select(r => r["Operation"] as string).ToList();
        // Join on a non-PK column (B.AId) and small tables → planner estimates a join op for INNER.
        Assert.Contains(ops, o => o != null && o.Contains("JOIN") && o.Contains("(INNER)"));
        // The chosen physical strategy is documented in the EXPLAIN details.
        var joinRow = result.Rows.First(r => (r["Operation"] as string)?.Contains("JOIN") == true);
        Assert.Contains("strategy=", joinRow["Details"] as string);
    }

    [Fact]
    public void Explain_PkEqualsPkJoin_EstimatesSortMerge()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T1 (Key INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE T2 (Key INT PRIMARY KEY, Data STRING)");

        var result = engine.Execute(
            "EXPLAIN SELECT t1.Val, t2.Data FROM T1 t1 INNER JOIN T2 t2 ON t1.Key = t2.Key");

        var ops = result.Rows.Select(r => r["Operation"] as string).ToList();
        // Both join columns are primary keys → planner predicts a sort-merge join.
        Assert.Contains("SORT_MERGE_JOIN (INNER)", ops);
        var joinRow = result.Rows.First(r => (r["Operation"] as string)?.Contains("JOIN") == true);
        Assert.Contains("strategy=sort-merge", joinRow["Details"] as string);
    }

    // ── CASE WHEN / CAST in SELECT ───────────────────────────────────────

    [Fact]
    public void CaseWhen_InSelect_ReturnsConditionalValue()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, NULL)");

        var result = engine.Execute(
            "SELECT CASE WHEN Val > 15 THEN 'High' WHEN Val IS NOT NULL THEN 'Low' ELSE 'None' END AS Label FROM T");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("Low", result.Rows[0]["Label"]);
        Assert.Equal("High", result.Rows[1]["Label"]);
        Assert.Equal("None", result.Rows[2]["Label"]);
    }

    // ── Scalar functions ──────────────────────────────────────────────────

    [Fact]
    public void Concat_CombinesStrings()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, First STRING, Last STRING)");
        engine.Execute("INSERT INTO T (Id, First, Last) VALUES (1, 'Hello', 'World')");

        var result = engine.Execute("SELECT CONCAT(First, ' ', Last) AS FullName FROM T");

        Assert.Equal("Hello World", result.Rows[0]["FullName"]);
    }

    [Fact]
    public void Upper_Lower_ChangeCase()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var result = engine.Execute("SELECT UPPER(Name) AS Up, LOWER(Name) AS Low FROM T");

        Assert.Equal("ALICE", result.Rows[0]["Up"]);
        Assert.Equal("alice", result.Rows[0]["Low"]);
    }

    [Fact]
    public void Trim_RemovesWhitespace()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, '  padded  ')");

        var result = engine.Execute("SELECT TRIM(Name) AS Clean FROM T");

        Assert.Equal("padded", result.Rows[0]["Clean"]);
    }

    [Fact]
    public void Length_ReturnsStringLength()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Hello')");

        var result = engine.Execute("SELECT LENGTH(Name) AS Len FROM T");

        Assert.Equal(5L, result.Rows[0]["Len"]);
    }

    [Fact]
    public void Substring_ExtractsPart()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Hello World')");

        var result = engine.Execute("SELECT SUBSTRING(Name, 1, 5) AS Part FROM T");

        Assert.Equal("Hello", result.Rows[0]["Part"]);
    }

    [Fact]
    public void ScalarFunction_InWhere_WorksAsFilter()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')");

        var result = engine.Execute("SELECT Id FROM T WHERE UPPER(Name) = 'ALICE'");

        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    // ── TOP / LIMIT Paging ───────────────────────────────────────────────

    [Fact]
    public void Top_Select_LimitsRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 30)");

        var result = engine.Execute("SELECT TOP 2 * FROM T ORDER BY Val DESC");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(30, result.Rows[0]["Val"]);
        Assert.Equal(20, result.Rows[1]["Val"]);
    }

    [Fact]
    public void Top_WithParentheses_LimitsRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        for (int i = 1; i <= 5; i++)
            engine.Execute($"INSERT INTO T (Id) VALUES ({i})");

        var result = engine.Execute("SELECT TOP (3) Id FROM T");
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void Limit_Select_LimitsRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        for (int i = 1; i <= 5; i++)
            engine.Execute($"INSERT INTO T (Id) VALUES ({i})");

        var result = engine.Execute("SELECT Id FROM T LIMIT 2");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Offset_Limit_PageSelect()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        for (int i = 1; i <= 5; i++)
            engine.Execute($"INSERT INTO T (Id) VALUES ({i})");

        var result = engine.Execute("SELECT Id FROM T ORDER BY Id OFFSET 2 ROWS FETCH NEXT 2 ROWS ONLY");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(3, result.Rows[0]["Id"]);
        Assert.Equal(4, result.Rows[1]["Id"]);
    }

    [Fact]
    public void Top_PreparedStatement_LimitsRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        for (int i = 1; i <= 10; i++)
            engine.Execute($"INSERT INTO T (Id, Val) VALUES ({i}, {i * 10})");

        var stmt = engine.Prepare("SELECT TOP 3 Val FROM T ORDER BY Val DESC");
        var result = stmt.Execute();
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(100, result.Rows[0]["Val"]);
        Assert.Equal(90, result.Rows[1]["Val"]);
        Assert.Equal(80, result.Rows[2]["Val"]);
    }

    // ── Recursive CTEs (WITH RECURSIVE) ───────────────────────────────────────

    [Fact]
    public void RecursiveCte_TreeTraversal_ParentChild()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Emp (Id INT PRIMARY KEY, Name STRING, ManagerId INT)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (1, 'Alice', NULL)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (2, 'Bob', 1)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (3, 'Carol', 1)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (4, 'Dave', 2)");

        var result = engine.Execute(@"
            WITH RECURSIVE cte AS (
                SELECT Id, Name FROM Emp WHERE ManagerId IS NULL
                UNION ALL
                SELECT e.Id, e.Name FROM Emp e JOIN cte ON e.ManagerId = cte.Id
            )
            SELECT * FROM cte");

        Assert.Equal(4, result.Rows.Count);
    }

    [Fact]
    public void RecursiveCte_UnionAll_PreservesDuplicates()
    {
        // Self-ref where a row can appear multiple times via different paths
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Edge (Src INT, Dst INT)");
        engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (1, 2)");
        engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (1, 3)");
        engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (2, 4)");
        engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (3, 4)");

        // Node 4 is reachable from 1 via two paths: 1→2→4 and 1→3→4
        // UNION ALL should produce 4 twice
        var result = engine.Execute(@"
            WITH RECURSIVE cte AS (
                SELECT Dst AS Node FROM Edge WHERE Src = 1
                UNION ALL
                SELECT e.Dst FROM Edge e JOIN cte ON e.Src = cte.Node
            )
            SELECT * FROM cte WHERE Node = 4");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void RecursiveCte_Union_Deduplicates()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Edge (Src INT, Dst INT)");
        engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (1, 2)");
        engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (1, 3)");
        engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (2, 4)");
        engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (3, 4)");

        // Same as above but UNION deduplicates → 4 appears once
        var result = engine.Execute(@"
            WITH RECURSIVE cte AS (
                SELECT Dst AS Node FROM Edge WHERE Src = 1
                UNION
                SELECT e.Dst FROM Edge e JOIN cte ON e.Src = cte.Node
            )
            SELECT * FROM cte WHERE Node = 4");

        Assert.Single(result.Rows);
    }

    [Fact]
    public void RecursiveCte_EmptyAnchor_ReturnsEmpty()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        // No rows inserted — anchor returns nothing

        var result = engine.Execute(@"
            WITH RECURSIVE cte AS (
                SELECT Id, Val FROM T WHERE Val > 100
                UNION ALL
                SELECT Id, Val+1 FROM cte WHERE Val < 200
            )
            SELECT * FROM cte");

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void RecursiveCte_SingleIteration_AnchorOnly()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");

        // Anchor returns 1 row, recursive member has a non-matching condition
        var result = engine.Execute(@"
            WITH RECURSIVE cte AS (
                SELECT Id, Val FROM T WHERE Val = 10
                UNION ALL
                SELECT Id, Val+1 FROM cte WHERE Val < 0
            )
            SELECT * FROM cte");

        Assert.Single(result.Rows);
        Assert.Equal(10, result.Rows[0]["Val"]);
    }

    [Fact]
    public void RecursiveCte_CycleDetection_ExceedsMaxIterations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wal_rcte_cycle_" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new WalhallaOptions(dir)
            {
                RecursiveCteMaxIterations = 5
            };
            using var engine = new WalhallaEngine(options);

            engine.Execute("CREATE TABLE Edge (Src INT, Dst INT)");
            engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (1, 2)");
            engine.Execute("INSERT INTO Edge (Src, Dst) VALUES (2, 1)"); // cycle

            var ex = Assert.Throws<WalhallaException>(() =>
                engine.Execute(@"
                    WITH RECURSIVE cte AS (
                        SELECT Src, Dst FROM Edge WHERE Src = 1
                        UNION ALL
                        SELECT e.Src, e.Dst FROM Edge e JOIN cte ON e.Src = cte.Dst
                    )
                    SELECT * FROM cte"));

            Assert.Contains("42P19", ex.SqlState);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* cleanup */ }
        }
    }

    [Fact]
    public void RecursiveCte_ConfigurableLimit_BreaksAtCustomThreshold()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wal_rcte_" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new WalhallaOptions(dir)
            {
                RecursiveCteMaxIterations = 3
            };
            using var engine = new WalhallaEngine(options);

            engine.Execute("CREATE TABLE T (N INT PRIMARY KEY)");
            engine.Execute("INSERT INTO T (N) VALUES (1)");

            // Anchor returns 1, then 2, then 3, then 4, ... — never terminates
            var ex = Assert.Throws<WalhallaException>(() =>
                engine.Execute(@"
                    WITH RECURSIVE cte AS (
                        SELECT N FROM T
                        UNION ALL
                        SELECT N+1 FROM cte WHERE N < 100
                    )
                    SELECT * FROM cte"));

            Assert.Contains("42P19", ex.SqlState);
            Assert.Contains("3", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* cleanup */ }
        }
    }

    [Fact]
    public void RecursiveCte_MainSelectFilter_AppliesAfterAccumulation()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Emp (Id INT PRIMARY KEY, Name STRING, ManagerId INT)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (1, 'Alice', NULL)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (2, 'Bob', 1)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (3, 'Carol', 1)");
        engine.Execute("INSERT INTO Emp (Id, Name, ManagerId) VALUES (4, 'Dave', 2)");

        // Filter in the main SELECT removes the root node
        var result = engine.Execute(@"
            WITH RECURSIVE cte AS (
                SELECT Id, Name FROM Emp WHERE ManagerId IS NULL
                UNION ALL
                SELECT e.Id, e.Name FROM Emp e JOIN cte ON e.ManagerId = cte.Id
            )
            SELECT * FROM cte WHERE Id <> 1");

        Assert.Equal(3, result.Rows.Count);
        // Alice (Id=1) should be excluded
        Assert.DoesNotContain(result.Rows, r => r["Id"] is int id && id == 1);
    }
}
