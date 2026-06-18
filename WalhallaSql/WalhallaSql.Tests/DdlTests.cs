using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class DdlTests
{
    // ── ALTER TABLE ──────────────────────────────────────────────────────────

    [Fact]
    public void AlterTable_AddColumn()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("ALTER TABLE T ADD COLUMN Age INT DEFAULT 30");

        var result = engine.Execute("SELECT Id, Name, Age FROM T");
        Assert.Single(result.Rows);
        Assert.Equal(30, result.Rows[0]["Age"]);
    }

    [Fact]
    public void AlterTable_DropColumn()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A INT, B INT)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, 10, 20)");

        engine.Execute("ALTER TABLE T DROP COLUMN B");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        // Should only have Id and A
    }

    [Fact]
    public void AlterTable_RenameColumn()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, OldName STRING)");
        engine.Execute("INSERT INTO T (Id, OldName) VALUES (1, 'test')");

        engine.Execute("ALTER TABLE T RENAME COLUMN OldName TO NewName");

        var result = engine.Execute("SELECT Id, NewName FROM T");
        Assert.Single(result.Rows);
        Assert.Equal("test", result.Rows[0]["NewName"]);
    }

    [Fact]
    public void AlterTable_RenameTable()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE OldTable (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO OldTable (Id, Val) VALUES (1, 'hello')");

        engine.Execute("ALTER TABLE OldTable RENAME TO NewTable");

        var result = engine.Execute("SELECT * FROM NewTable");
        Assert.Single(result.Rows);
    }

    // ── CREATE VIEW / DROP VIEW ──────────────────────────────────────────────

    [Fact]
    public void CreateView_SimpleSelect()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Age INT)");
        engine.Execute("INSERT INTO T (Id, Name, Age) VALUES (1, 'Alice', 30)");
        engine.Execute("INSERT INTO T (Id, Name, Age) VALUES (2, 'Bob', 25)");
        engine.Execute("INSERT INTO T (Id, Name, Age) VALUES (3, 'Charlie', 35)");

        engine.Execute("CREATE VIEW Adults AS SELECT Name, Age FROM T WHERE Age >= 30");

        var result = engine.Execute("SELECT * FROM Adults");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void DropView_RemovesView()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'x')");
        engine.Execute("CREATE VIEW V AS SELECT * FROM T");
        engine.Execute("DROP VIEW V");

        Assert.Throws<WalhallaException>(() => engine.Execute("SELECT * FROM V"));
    }

    [Fact]
    public void View_WithJoin()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE)");
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100.0)");

        engine.Execute(
            "CREATE VIEW OrderDetails AS SELECT c.Name, o.Amount FROM Customers c INNER JOIN Orders o ON c.Id = o.CustomerId");

        var result = engine.Execute("SELECT * FROM OrderDetails");
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0]["Name"]);
    }

    // ── FOREIGN KEYS ─────────────────────────────────────────────────────────

    [Fact]
    public void ForeignKey_InsertValidReference()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Parents (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute(
            "CREATE TABLE Children (Id INT PRIMARY KEY, ParentId INT, Name STRING, FOREIGN KEY (ParentId) REFERENCES Parents(Id))");

        engine.Execute("INSERT INTO Parents (Id, Name) VALUES (1, 'Parent1')");
        engine.Execute("INSERT INTO Children (Id, ParentId, Name) VALUES (1, 1, 'Child1')");

        var result = engine.Execute("SELECT * FROM Children");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void ForeignKey_InsertOrphan_Throws()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Parents (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute(
            "CREATE TABLE Children (Id INT PRIMARY KEY, ParentId INT, Name STRING, FOREIGN KEY (ParentId) REFERENCES Parents(Id))");

        Assert.Throws<WalhallaException>(() =>
            engine.Execute("INSERT INTO Children (Id, ParentId, Name) VALUES (1, 999, 'Orphan')"));
    }

    [Fact]
    public void ForeignKey_DeleteParent_RestrictThrows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Parents (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute(
            "CREATE TABLE Children (Id INT PRIMARY KEY, ParentId INT, Name STRING, FOREIGN KEY (ParentId) REFERENCES Parents(Id))");
        engine.Execute("INSERT INTO Parents (Id, Name) VALUES (1, 'Parent1')");
        engine.Execute("INSERT INTO Children (Id, ParentId, Name) VALUES (1, 1, 'Child1')");

        Assert.Throws<WalhallaException>(() =>
            engine.Execute("DELETE FROM Parents WHERE Id = 1"));
    }

    [Fact]
    public void ForeignKey_DeleteParent_CascadeDeletesChild()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Parents (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute(
            "CREATE TABLE Children (Id INT PRIMARY KEY, ParentId INT, Name STRING, FOREIGN KEY (ParentId) REFERENCES Parents(Id) ON DELETE CASCADE)");
        engine.Execute("INSERT INTO Parents (Id, Name) VALUES (1, 'Parent1')");
        engine.Execute("INSERT INTO Children (Id, ParentId, Name) VALUES (1, 1, 'Child1')");

        engine.Execute("DELETE FROM Parents WHERE Id = 1");

        var children = engine.Execute("SELECT * FROM Children");
        Assert.Empty(children.Rows);
    }

    [Fact]
    public void ForeignKey_UpdateFkToNonexistent_Throws()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Parents (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute(
            "CREATE TABLE Children (Id INT PRIMARY KEY, ParentId INT, Name STRING, FOREIGN KEY (ParentId) REFERENCES Parents(Id))");
        engine.Execute("INSERT INTO Parents (Id, Name) VALUES (1, 'Parent1')");
        engine.Execute("INSERT INTO Children (Id, ParentId, Name) VALUES (1, 1, 'Child1')");

        Assert.Throws<WalhallaException>(() =>
            engine.Execute("UPDATE Children SET ParentId = 999 WHERE Id = 1"));
    }

    [Fact]
    public void ForeignKey_DeleteUnreferenced_Succeeds()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Parents (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute(
            "CREATE TABLE Children (Id INT PRIMARY KEY, ParentId INT, Name STRING, FOREIGN KEY (ParentId) REFERENCES Parents(Id))");
        engine.Execute("INSERT INTO Parents (Id, Name) VALUES (1, 'Parent1')");
        engine.Execute("INSERT INTO Parents (Id, Name) VALUES (2, 'Parent2')");

        engine.Execute("DELETE FROM Parents WHERE Id = 2");

        Assert.Single(engine.Execute("SELECT * FROM Parents").Rows);
    }
}
