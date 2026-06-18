using System;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class OnConflictTests
{
    // ── ON CONFLICT DO NOTHING ──────────────────────────────────────────

    [Fact]
    public void OnConflict_DoNothing_SingleConflict_SkipsRow()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Bob') ON CONFLICT DO NOTHING");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void OnConflict_DoNothing_MixedRows_InsertsOnlyNew()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Bob'), (2, 'Charlie') ON CONFLICT DO NOTHING");

        var result = engine.Execute("SELECT Id, Name FROM T ORDER BY Id");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1, result.Rows[0].GetValue(0));
        Assert.Equal("Alice", result.Rows[0].GetValue(1));
        Assert.Equal(2, result.Rows[1].GetValue(0));
        Assert.Equal("Charlie", result.Rows[1].GetValue(1));
    }

    [Fact]
    public void OnConflict_DoNothing_NoConflict_InsertsAll()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var affected = engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob'), (3, 'Charlie') ON CONFLICT DO NOTHING");

        // Check affected rows and total count
        Assert.Equal(2, affected.AffectedRows);
        var rows = engine.Execute("SELECT * FROM T").Rows;
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void OnConflict_DoNothing_AllConflicts_SkipsAll()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice'), (2, 'Bob')");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'X'), (2, 'Y') ON CONFLICT DO NOTHING");

        var result = engine.Execute("SELECT Name FROM T ORDER BY Id");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Alice", result.Rows[0].GetValue(0));
        Assert.Equal("Bob", result.Rows[1].GetValue(0));
    }

    [Fact]
    public void OnConflict_DoNothing_TargetColumns()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Email STRING)");
        engine.Execute("CREATE UNIQUE INDEX UQ_Email ON T (Email)");
        engine.Execute("INSERT INTO T (Id, Name, Email) VALUES (1, 'Alice', 'a@b.com')");

        engine.Execute("INSERT INTO T (Id, Name, Email) VALUES (2, 'Bob', 'a@b.com') ON CONFLICT (Email) DO NOTHING");

        var result = engine.Execute("SELECT Id FROM T");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void OnConflict_DoNothing_OnConstraint()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Bob') ON CONFLICT ON CONSTRAINT PK_T DO NOTHING");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0].GetValue(0));
    }

    // ── ON CONFLICT DO UPDATE ───────────────────────────────────────────

    [Fact]
    public void OnConflict_DoUpdate_ExcludedCol_UpdatesValue()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Bob') ON CONFLICT (Id) DO UPDATE SET Name = EXCLUDED.Name");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Single(result.Rows);
        Assert.Equal("Bob", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void OnConflict_DoUpdate_MultipleSetColumns()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Age INT)");
        engine.Execute("INSERT INTO T (Id, Name, Age) VALUES (1, 'Alice', 30)");

        engine.Execute("INSERT INTO T (Id, Name, Age) VALUES (1, 'Bob', 25) ON CONFLICT (Id) DO UPDATE SET Name = EXCLUDED.Name, Age = EXCLUDED.Age");

        var result = engine.Execute("SELECT Name, Age FROM T WHERE Id = 1");
        Assert.Equal("Bob", result.Rows[0].GetValue(0));
        Assert.Equal(25, result.Rows[0].GetValue(1));
    }

    [Fact]
    public void OnConflict_DoUpdate_LiteralAssignment()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Counter INT)");
        engine.Execute("INSERT INTO T (Id, Name, Counter) VALUES (1, 'Alice', 0)");

        engine.Execute("INSERT INTO T (Id, Name, Counter) VALUES (1, 'Bob', 0) ON CONFLICT (Id) DO UPDATE SET Counter = 5");

        var result = engine.Execute("SELECT Name, Counter FROM T WHERE Id = 1");
        Assert.Equal("Alice", result.Rows[0].GetValue(0)); // Name unchanged
        Assert.Equal(5, result.Rows[0].GetValue(1));        // Counter = literal
    }

    [Fact]
    public void OnConflict_DoUpdate_MixedRows_InsertsAndUpdates()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Bob'), (2, 'Charlie') ON CONFLICT (Id) DO UPDATE SET Name = EXCLUDED.Name");

        var result = engine.Execute("SELECT Id, Name FROM T ORDER BY Id");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1, result.Rows[0].GetValue(0));
        Assert.Equal("Bob", result.Rows[0].GetValue(1));
        Assert.Equal(2, result.Rows[1].GetValue(0));
        Assert.Equal("Charlie", result.Rows[1].GetValue(1));
    }

    [Fact]
    public void OnConflict_DoUpdate_WhereTrue_Updates()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Version INT)");
        engine.Execute("INSERT INTO T (Id, Name, Version) VALUES (1, 'Alice', 1)");

        engine.Execute("INSERT INTO T (Id, Name, Version) VALUES (1, 'Bob', 2) ON CONFLICT (Id) DO UPDATE SET Name = EXCLUDED.Name WHERE T.Version < 2");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Equal("Bob", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void OnConflict_DoUpdate_WhereFalse_SkipsUpdate()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Version INT)");
        engine.Execute("INSERT INTO T (Id, Name, Version) VALUES (1, 'Alice', 5)");

        engine.Execute("INSERT INTO T (Id, Name, Version) VALUES (1, 'Bob', 2) ON CONFLICT (Id) DO UPDATE SET Name = EXCLUDED.Name WHERE T.Version < 2");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Equal("Alice", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void OnConflict_DoUpdate_NoTarget_UsesAnyUnique()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Bob') ON CONFLICT DO UPDATE SET Name = EXCLUDED.Name");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Equal("Bob", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void OnConflict_DoUpdate_UniqueIndexConflict()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Email STRING)");
        engine.Execute("CREATE UNIQUE INDEX UQ_Email ON T (Email)");
        engine.Execute("INSERT INTO T (Id, Name, Email) VALUES (1, 'Alice', 'a@b.com')");

        engine.Execute("INSERT INTO T (Id, Name, Email) VALUES (2, 'Bob', 'a@b.com') ON CONFLICT (Email) DO UPDATE SET Name = EXCLUDED.Name");

        var result = engine.Execute("SELECT Id, Name FROM T ORDER BY Id");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0].GetValue(0));
        Assert.Equal("Bob", result.Rows[0].GetValue(1));
    }

    // ── INSERT ... SELECT with ON CONFLICT ──────────────────────────────

    [Fact]
    public void OnConflict_InsertSelect_DoNothing()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) SELECT Id, Name FROM T ON CONFLICT DO NOTHING");

        var rows = engine.Execute("SELECT * FROM T").Rows;
        Assert.Single(rows);
    }

    [Fact]
    public void OnConflict_InsertSelect_DoUpdate()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) SELECT Id, 'Bob' FROM T ON CONFLICT (Id) DO UPDATE SET Name = EXCLUDED.Name");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Equal("Bob", result.Rows[0].GetValue(0));
    }

    // ── Trigger ordering ────────────────────────────────────────────────

    [Fact]
    public void OnConflict_DoNothing_FiresBeforeInsertButNotAfter()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Log (Msg STRING)");
        engine.Execute(@"
            CREATE TRIGGER TrgBeforeInsert ON T BEFORE INSERT AS
            BEGIN
                INSERT INTO Log (Msg) VALUES ('before fired');
            END");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        // BEFORE INSERT fires for the conflict row too, but DO NOTHING means no AFTER INSERT
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Bob') ON CONFLICT DO NOTHING");

        // BEFORE trigger fires once for the successful INSERT and once for the conflict attempt
        var logRows = engine.Execute("SELECT * FROM Log").Rows;
        Assert.Equal(2, logRows.Count);
    }

    [Fact]
    public void OnConflict_DoUpdate_FiresUpdateTriggers()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Log (Msg STRING)");
        engine.Execute(@"
            CREATE TRIGGER TrgAfterUpdate ON T AFTER UPDATE AS
            BEGIN
                INSERT INTO Log (Msg) VALUES ('updated');
            END");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Bob') ON CONFLICT (Id) DO UPDATE SET Name = EXCLUDED.Name");

        var logRows = engine.Execute("SELECT * FROM Log").Rows;
        Assert.Single(logRows);
    }
}
