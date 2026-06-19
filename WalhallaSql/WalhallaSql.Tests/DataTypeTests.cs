using System;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class DataTypeTests
{
    // ── BOOLEAN ────────────────────────────────────────────────────────────

    [Fact]
    public void Boolean_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Flag BOOL)");
        engine.Execute("INSERT INTO T (Id, Flag) VALUES (1, TRUE)");
        engine.Execute("INSERT INTO T (Id, Flag) VALUES (2, FALSE)");

        var result = engine.Execute("SELECT * FROM T WHERE Flag = TRUE");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void Boolean_AcceptsNumericLiterals()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Flag BIT)");
        engine.Execute("INSERT INTO T (Id, Flag) VALUES (1, 1)");
        engine.Execute("INSERT INTO T (Id, Flag) VALUES (2, 0)");

        var truthy = engine.Execute("SELECT Id FROM T WHERE Flag = TRUE");
        Assert.Single(truthy.Rows);
        Assert.Equal(1, truthy.Rows[0]["Id"]);

        var falsy = engine.Execute("SELECT Id FROM T WHERE Flag = FALSE");
        Assert.Single(falsy.Rows);
        Assert.Equal(2, falsy.Rows[0]["Id"]);
    }

    [Fact]
    public void Boolean_FalseCondition()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Active BOOL)");
        engine.Execute("INSERT INTO T (Id, Active) VALUES (1, TRUE)");
        engine.Execute("INSERT INTO T (Id, Active) VALUES (2, FALSE)");

        var result = engine.Execute("SELECT Id FROM T WHERE Active = FALSE");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void Boolean_TruthyFilter()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Valid BOOL)");
        engine.Execute("INSERT INTO T (Id, Valid) VALUES (1, TRUE)");
        engine.Execute("INSERT INTO T (Id, Valid) VALUES (2, FALSE)");

        var result = engine.Execute("SELECT Id FROM T WHERE Valid");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void Boolean_NotTruthyFilter()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Valid BOOL)");
        engine.Execute("INSERT INTO T (Id, Valid) VALUES (1, TRUE)");
        engine.Execute("INSERT INTO T (Id, Valid) VALUES (2, FALSE)");

        var result = engine.Execute("SELECT Id FROM T WHERE NOT Valid");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    // ── DATETIME ───────────────────────────────────────────────────────────

    [Fact]
    public void DateTime_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Created DATETIME)");
        engine.Execute("INSERT INTO T (Id, Created) VALUES (1, '2024-01-15')");
        engine.Execute("INSERT INTO T (Id, Created) VALUES (2, '2024-06-20')");

        var result = engine.Execute("SELECT Created FROM T WHERE Id = 1");

        Assert.Single(result.Rows);
        Assert.NotNull(result.Rows[0]["Created"]);
    }

    [Fact]
    public void DateTime_EqualityComparison()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Created DATETIME)");
        engine.Execute("INSERT INTO T (Id, Created) VALUES (1, '2024-01-15')");
        engine.Execute("INSERT INTO T (Id, Created) VALUES (2, '2024-06-20')");

        var result = engine.Execute("SELECT Created FROM T WHERE Id = 1");

        Assert.Single(result.Rows);
        Assert.NotNull(result.Rows[0]["Created"]);
    }

    [Fact]
    public void DateTime_InsertRetrieve()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Created DATETIME)");
        engine.Execute("INSERT INTO T (Id, Created) VALUES (1, '2023-01-01')");
        engine.Execute("INSERT INTO T (Id, Created) VALUES (2, '2024-06-01')");
        engine.Execute("INSERT INTO T (Id, Created) VALUES (3, '2025-12-31')");

        var result = engine.Execute("SELECT Id, Created FROM T ORDER BY Id");
        Assert.Equal(3, result.Rows.Count);
        Assert.NotNull(result.Rows[0]["Created"]);
        Assert.NotNull(result.Rows[1]["Created"]);
        Assert.NotNull(result.Rows[2]["Created"]);
    }

    [Fact]
    public void DateTime_TimestampFormat()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Ts TIMESTAMP)");
        engine.Execute("INSERT INTO T (Id, Ts) VALUES (1, '2024-01-15 10:30:00')");

        var result = engine.Execute("SELECT Ts FROM T WHERE Id = 1");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void DateTime_DateType()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, D DATE)");
        engine.Execute("INSERT INTO T (Id, D) VALUES (1, '2024-01-15')");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
    }

    // ── DECIMAL ────────────────────────────────────────────────────────────

    [Fact]
    public void Decimal_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Price DECIMAL)");
        engine.Execute("INSERT INTO T (Id, Price) VALUES (1, 19.99)");
        engine.Execute("INSERT INTO T (Id, Price) VALUES (2, 49.95)");

        var result = engine.Execute("SELECT * FROM T WHERE Price > 20");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void Decimal_MoneyType()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Amount MONEY)");
        engine.Execute("INSERT INTO T (Id, Amount) VALUES (1, 100.50)");
        engine.Execute("INSERT INTO T (Id, Amount) VALUES (2, 200.75)");

        var result = engine.Execute("SELECT Amount FROM T WHERE Id = 2");
        Assert.Equal(200.75m, (decimal)result.Rows[0]["Amount"]!);
    }

    [Fact]
    public void Decimal_NumericType()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val NUMERIC)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 123.456)");

        var result = engine.Execute("SELECT Val FROM T");
        Assert.Equal(123.456m, result.Rows[0]["Val"]);
    }

    [Fact]
    public void Decimal_ArithmeticInSelect()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A DECIMAL, B DECIMAL)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, 10.5, 2.5)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (2, 20.0, 10.0)");

        var result = engine.Execute("SELECT A + B AS Total FROM T WHERE Id = 1");
        Assert.Equal(13.0, (double)result.Rows[0]["Total"]!);
    }

    // ── INT64 / BIGINT ─────────────────────────────────────────────────────

    [Fact]
    public void BigInt_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, BigVal BIGINT)");
        engine.Execute("INSERT INTO T (Id, BigVal) VALUES (1, 1234567890123456789)");

        var result = engine.Execute("SELECT BigVal FROM T WHERE Id = 1");
        Assert.Equal(1234567890123456789L, (long)result.Rows[0]["BigVal"]!);
    }

    // ── GUID / UNIQUEIDENTIFIER ────────────────────────────────────────────

    [Fact]
    public void Guid_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Uid UUID)");
        engine.Execute("INSERT INTO T (Id, Uid) VALUES (1, 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11')");

        var result = engine.Execute("SELECT Uid FROM T WHERE Id = 1");
        Assert.NotNull(result.Rows[0]["Uid"]);
    }

    // ── Cross-type comparison ──────────────────────────────────────────────

    [Fact]
    public void CrossType_IntComparedToString()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Key STRING)");
        engine.Execute("INSERT INTO T (Id, Key) VALUES (1, '42')");
        engine.Execute("INSERT INTO T (Id, Key) VALUES (2, 'other')");

        var result = engine.Execute("SELECT Id FROM T WHERE Key = 42");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void CrossType_DoubleComparedToIntColumn()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val DOUBLE)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 5.0)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 10.0)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val > 7");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void SmallInt_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val SMALLINT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 100)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 200)");

        var result = engine.Execute("SELECT * FROM T WHERE Val = 100");
        Assert.Single(result.Rows);
        Assert.Equal((short)100, result.Rows[0]["Val"]);
    }

    [Fact]
    public void Date_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, D DATE)");
        engine.Execute("INSERT INTO T (Id, D) VALUES (1, '2024-03-15')");

        var result = engine.Execute("SELECT * FROM T WHERE D = '2024-03-15'");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Time_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, T TIME)");
        engine.Execute("INSERT INTO T (Id, T) VALUES (1, '14:30:00')");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
    }

    // ── BINARY / VARBINARY ───────────────────────────────────────────────────

    [Fact]
    public void Binary_CreateAndInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data VARBINARY)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, X'010203')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, X'FFFE')");

        var result = engine.Execute("SELECT Data FROM T WHERE Id = 1");
        Assert.Single(result.Rows);
        var bytes = Assert.IsType<byte[]>(result.Rows[0]["Data"]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, bytes);
    }

    [Fact]
    public void Binary_LargeValue_RoundTrip()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data VARBINARY)");

        var payload = new byte[5000];
        new Random(42).NextBytes(payload);
        var hex = Convert.ToHexString(payload);
        engine.Execute($"INSERT INTO T (Id, Data) VALUES (1, X'{hex}')");

        var result = engine.Execute("SELECT Data FROM T WHERE Id = 1");
        Assert.Single(result.Rows);
        var bytes = Assert.IsType<byte[]>(result.Rows[0]["Data"]);
        Assert.Equal(payload, bytes);
    }
}
