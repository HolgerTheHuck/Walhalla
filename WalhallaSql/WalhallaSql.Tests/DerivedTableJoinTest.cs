using System.Linq;
using Xunit;

namespace WalhallaSql.Tests;

public class DerivedTableJoinTest
{
    [Fact]
    public void Join_With_Derived_Table_Left_Join()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DECIMAL)");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100.0)");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 1, 200.0)");

        var result = engine.Execute(@"
            SELECT c.Name, __j0.total
            FROM Customers c
            LEFT JOIN (SELECT CustomerId, SUM(Amount) AS total FROM Orders GROUP BY CustomerId) __j0
            ON c.Id = __j0.CustomerId");

        Assert.Equal(2, result.Rows.Count);
        var row0 = result.Rows[0].Values.ToArray();
        var row1 = result.Rows[1].Values.ToArray();
        Assert.Equal("Alice", row0[0]);
        Assert.Equal("300", row0[1]);
        Assert.Equal("Bob", row1[0]);
        Assert.Null(row1[1]);
    }

    [Fact]
    public void Join_With_Derived_Table_Inner_Join()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, AId INT, Val INT)");
        engine.Execute("INSERT INTO A (Id, Name) VALUES (1, 'X')");
        engine.Execute("INSERT INTO A (Id, Name) VALUES (2, 'Y')");
        engine.Execute("INSERT INTO B (Id, AId, Val) VALUES (1, 1, 10)");
        engine.Execute("INSERT INTO B (Id, AId, Val) VALUES (2, 1, 20)");

        var result = engine.Execute(@"
            SELECT a.Name, j.total
            FROM A a
            INNER JOIN (SELECT AId, SUM(Val) AS total FROM B GROUP BY AId) j
            ON a.Id = j.AId");

        Assert.Single(result.Rows);
        var row0 = result.Rows[0].Values.ToArray();
        Assert.Equal("X", row0[0]);
        Assert.Equal("30", row0[1]);
    }

    [Fact]
    public void Join_With_Derived_Table_No_Alias_Keyword()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T1 (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE T2 (Id INT PRIMARY KEY, RefId INT)");
        engine.Execute("INSERT INTO T1 (Id, Name) VALUES (1, 'A')");
        engine.Execute("INSERT INTO T2 (Id, RefId) VALUES (1, 1)");

        var result = engine.Execute(@"
            SELECT t1.Name
            FROM T1 t1
            JOIN (SELECT RefId FROM T2) dt
            ON t1.Id = dt.RefId");

        Assert.Single(result.Rows);
        Assert.Equal("A", result.Rows[0].Values.ToArray()[0]);
    }
}
