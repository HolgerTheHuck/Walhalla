using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class ProcedureTriggerTests
{
    // ── CREATE / DROP PROCEDURE ──────────────────────────────────────────────

    [Fact]
    public void CreateProcedure_BasicInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Log (Id INT PRIMARY KEY, Message STRING)");

        engine.Execute(@"
            CREATE PROCEDURE InsertLog
                @msg STRING
            AS
            BEGIN
                INSERT INTO Log (Id, Message) VALUES (1, @msg);
            END");

        engine.Execute("EXEC InsertLog @msg = 'Hello'");

        var result = engine.Execute("SELECT * FROM Log");
        Assert.Single(result.Rows);
        Assert.Equal("Hello", result.Rows[0]["Message"]);
    }

    [Fact]
    public void DropProcedure_RemovesDefinition()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE PROCEDURE Dummy AS BEGIN SELECT 1; END");
        engine.Execute("DROP PROCEDURE Dummy");

        Assert.Throws<WalhallaException>(() =>
            engine.Execute("EXEC Dummy"));
    }

    [Fact]
    public void DropProcedure_IfExists_NotFound()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("DROP PROCEDURE IF EXISTS NonExisting");
        // Should not throw
    }

    [Fact]
    public void CreateProcedure_OrReplace()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");

        engine.Execute("CREATE PROCEDURE P AS BEGIN INSERT INTO T (Id, Val) VALUES (1, 'old'); END");
        engine.Execute("CREATE OR REPLACE PROCEDURE P AS BEGIN INSERT INTO T (Id, Val) VALUES (2, 'new'); END");
        engine.Execute("EXEC P");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
        Assert.Equal("new", result.Rows[0]["Val"]);
    }

    [Fact]
    public void Exec_PositionalParameters()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A INT, B STRING)");

        engine.Execute(@"
            CREATE PROCEDURE InsertPair
                @a INT,
                @b STRING
            AS
            BEGIN
                INSERT INTO T (Id, A, B) VALUES (1, @a, @b);
            END");

        engine.Execute("EXEC InsertPair 42, 'positional'");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal(42, result.Rows[0]["A"]);
        Assert.Equal("positional", result.Rows[0]["B"]);
    }

    [Fact]
    public void Exec_DefaultParameterValue()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");

        engine.Execute(@"
            CREATE PROCEDURE InsertDefaultName
                @name STRING = 'anonymous'
            AS
            BEGIN
                INSERT INTO T (Id, Name) VALUES (1, @name);
            END");

        engine.Execute("EXEC InsertDefaultName");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Equal("anonymous", result.Rows[0]["Name"]);
    }

    [Fact]
    public void Proc_MultipleStatements_InOrder()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");

        engine.Execute(@"
            CREATE PROCEDURE MultiInsert
            AS
            BEGIN
                INSERT INTO T (Id, Val) VALUES (1, 10);
                INSERT INTO T (Id, Val) VALUES (2, 20);
                INSERT INTO T (Id, Val) VALUES (3, 30);
            END");

        engine.Execute("EXEC MultiInsert");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Equal(3, result.Rows.Count);
    }

    // ── CREATE / DROP TRIGGER ────────────────────────────────────────────────

    [Fact]
    public void Trigger_AfterInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, Amount DOUBLE)");
        engine.Execute("CREATE TABLE AuditLog (Id INT PRIMARY KEY, Message STRING)");

        engine.Execute(@"
            CREATE TRIGGER trg_AfterInsert ON Orders AFTER INSERT AS
            BEGIN
                INSERT INTO AuditLog (Id, Message) VALUES (1, 'Record inserted');
            END");

        engine.Execute("INSERT INTO Orders (Id, Amount) VALUES (1, 99.99)");

        var log = engine.Execute("SELECT * FROM AuditLog");
        Assert.Single(log.Rows);
    }

    [Fact]
    public void Trigger_BeforeInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE Log (Id INT PRIMARY KEY, Msg STRING)");

        engine.Execute(@"
            CREATE TRIGGER trg_BeforeInsert ON T BEFORE INSERT AS
            BEGIN
                INSERT INTO Log (Id, Msg) VALUES (1, 'about to insert');
            END");

        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'test')");

        Assert.Single(engine.Execute("SELECT * FROM Log").Rows);
    }

    [Fact]
    public void Trigger_DropRemoves()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");

        engine.Execute(@"
            CREATE TRIGGER trg_Test ON T AFTER INSERT AS
            BEGIN
                INSERT INTO T (Id, Val) VALUES (99, 'from trigger');
            END");

        engine.Execute("DROP TRIGGER trg_Test");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'direct')");

        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    [Fact]
    public void Trigger_ExecStatement_DoubleParameter_Success()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A INT, B INT)");

        engine.Execute(@"
            CREATE PROCEDURE InsertPair
                @a INT,
                @b INT
            AS
            BEGIN
                INSERT INTO T (Id, A, B) VALUES (1, @a, @b);
            END");

        engine.Execute("EXEC InsertPair @a = 5, @b = 7");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Equal(5, result.Rows[0]["A"]);
        Assert.Equal(7, result.Rows[0]["B"]);
    }

    [Fact]
    public void Trigger_AfterInsert_MultiRow_UsesInsertedTable()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, OrderDate DATE)");
        engine.Execute("CREATE TABLE AuditLog (Id INT PRIMARY KEY, Message STRING)");

        engine.Execute(@"
            CREATE TRIGGER trg_AfterOrderInsert
            ON Orders AFTER INSERT AS
            BEGIN
                INSERT INTO AuditLog (Id, Message)
                SELECT INSERTED.Id, 'Neue Bestellung eingefuegt'
                FROM INSERTED;
            END");

        engine.Execute(@"
            INSERT INTO Orders (Id, CustomerId, OrderDate)
            VALUES (1, 1, '2026-06-01'),
                   (2, 1, '2026-06-10'),
                   (3, 2, '2026-06-15'),
                   (4, 99, '2026-06-20');");

        var orders = engine.Execute("SELECT * FROM Orders");
        Assert.Equal(4, orders.Rows.Count);

        var audit = engine.Execute("SELECT * FROM AuditLog ORDER BY Id");
        Assert.Equal(4, audit.Rows.Count);
        Assert.Equal(1, audit.Rows[0]["Id"]);
        Assert.Equal(2, audit.Rows[1]["Id"]);
        Assert.Equal(3, audit.Rows[2]["Id"]);
        Assert.Equal(4, audit.Rows[3]["Id"]);
        Assert.Equal("Neue Bestellung eingefuegt", audit.Rows[0]["Message"]);
    }
}
