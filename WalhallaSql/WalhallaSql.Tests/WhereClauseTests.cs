using System;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class WhereClauseTests
{
    // ── IS NULL / IS NOT NULL ──────────────────────────────────────────────

    [Fact]
    public void IsNull_FindsNullValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 'hello')");

        var result = engine.Execute("SELECT Id FROM T WHERE Val IS NULL");

        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void IsNull_WhereNotNull()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 'hello')");

        var result = engine.Execute("SELECT Id FROM T WHERE Val IS NOT NULL");

        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void IsNull_AllRowsMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");
        engine.Execute("INSERT INTO T (Id) VALUES (2)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val IS NULL");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void IsNull_NoRowsMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'a')");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 'b')");

        var result = engine.Execute("SELECT Id FROM T WHERE Val IS NULL");

        Assert.Empty(result.Rows);
    }

    // ── BETWEEN / NOT BETWEEN ──────────────────────────────────────────────

    [Fact]
    public void Between_InclusiveRange()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 30)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (4, 40)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val BETWEEN 15 AND 35");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Between_BoundaryValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 30)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val BETWEEN 10 AND 30");

        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void Between_NoMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val BETWEEN 50 AND 100");

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void NotBetween_ExcludesRange()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 5)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 15)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 25)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val NOT BETWEEN 10 AND 20");

        Assert.Equal(2, result.Rows.Count);
    }

    // ── IN (value list) / NOT IN ───────────────────────────────────────────

    [Fact]
    public void InList_MatchesValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (3, 'Charlie')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (4, 'Diana')");

        var result = engine.Execute("SELECT Id FROM T WHERE Name IN ('Alice', 'Charlie')");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void InList_IntValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 1)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 2)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 3)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (4, 4)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val IN (1, 3, 5)");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void InList_NoMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 1)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 2)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val IN (10, 20)");

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void NotInList_ExcludesValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 1)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 2)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 3)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val NOT IN (1, 3)");

        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    // ── IN (subquery) / NOT IN (subquery) ──────────────────────────────────

    [Fact]
    public void InSubquery_FindsMatches()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T1 (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("CREATE TABLE T2 (Val INT)");
        engine.Execute("INSERT INTO T1 (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T1 (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T1 (Id, Val) VALUES (3, 30)");
        engine.Execute("INSERT INTO T2 (Val) VALUES (10)");
        engine.Execute("INSERT INTO T2 (Val) VALUES (30)");

        var result = engine.Execute("SELECT Id FROM T1 WHERE Val IN (SELECT Val FROM T2)");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void InSubquery_NoMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T1 (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("CREATE TABLE T2 (Val INT)");
        engine.Execute("INSERT INTO T1 (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T2 (Val) VALUES (99)");

        var result = engine.Execute("SELECT Id FROM T1 WHERE Val IN (SELECT Val FROM T2)");

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void NotInSubquery_ExcludesMatches()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T1 (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("CREATE TABLE T2 (Val INT)");
        engine.Execute("INSERT INTO T1 (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T1 (Id, Val) VALUES (2, 20)");
        engine.Execute("INSERT INTO T2 (Val) VALUES (10)");

        var result = engine.Execute("SELECT Id FROM T1 WHERE Val NOT IN (SELECT Val FROM T2)");

        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    // ── LIKE / NOT LIKE ────────────────────────────────────────────────────

    [Fact]
    public void NotLike_ExcludesMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')");

        var result = engine.Execute("SELECT Id FROM T WHERE Name NOT LIKE 'A%'");

        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    // ── NOT EXISTS ─────────────────────────────────────────────────────────

    [Fact]
    public void NotExists_Uncorrelated()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        var result = engine.Execute(
            "SELECT Id FROM T WHERE NOT EXISTS (SELECT 1 FROM T AS T2 WHERE T2.Val > 100)");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void NotExists_AllRowsFiltered()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        var result = engine.Execute(
            "SELECT Id FROM T WHERE NOT EXISTS (SELECT 1 FROM T AS T2 WHERE T2.Val < 100)");

        Assert.Empty(result.Rows);
    }

    // ── Arithmetic in WHERE ────────────────────────────────────────────────

    [Fact]
    public void ArithmeticInWhere_Add()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A INT, B INT)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, 10, 5)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (2, 3, 2)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (3, 7, 8)");

        var result = engine.Execute("SELECT Id FROM T WHERE A + B > 10");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void ArithmeticInWhere_Multiply()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Price DOUBLE, Qty INT)");
        engine.Execute("INSERT INTO T (Id, Price, Qty) VALUES (1, 10.0, 5)");
        engine.Execute("INSERT INTO T (Id, Price, Qty) VALUES (2, 5.0, 2)");
        engine.Execute("INSERT INTO T (Id, Price, Qty) VALUES (3, 20.0, 1)");

        var result = engine.Execute("SELECT Id FROM T WHERE Price * Qty < 50");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void ArithmeticInWhere_Subtract()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Max INT, Min INT)");
        engine.Execute("INSERT INTO T (Id, Max, Min) VALUES (1, 100, 20)");
        engine.Execute("INSERT INTO T (Id, Max, Min) VALUES (2, 10, 5)");
        engine.Execute("INSERT INTO T (Id, Max, Min) VALUES (3, 50, 40)");

        var result = engine.Execute("SELECT Id FROM T WHERE Max - Min > 50");

        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void ArithmeticInWhere_Divide()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A DOUBLE, B DOUBLE)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, 100.0, 2.0)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (2, 10.0, 5.0)");

        var result = engine.Execute("SELECT Id FROM T WHERE A / B > 20");

        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void ArithmeticInWhere_Modulo()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 7)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 8)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 9)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val % 2 = 0");

        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void ArithmeticInWhere_UnaryMinus()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, -5)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, -3)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val < 0");

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void ArithmeticInSelect_AddColumns()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, A INT, B INT)");
        engine.Execute("INSERT INTO T (Id, A, B) VALUES (1, 10, 5)");

        var result = engine.Execute("SELECT A + B AS Sum FROM T");

        Assert.Single(result.Rows);
        Assert.Equal(15, Convert.ToInt32(result.Rows[0]["Sum"]));
    }

    [Fact]
    public void ArithmeticInSelect_Multiply()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Price DOUBLE, Qty INT)");
        engine.Execute("INSERT INTO T (Id, Price, Qty) VALUES (1, 12.5, 4)");

        var result = engine.Execute("SELECT Price * Qty AS Total FROM T");

        Assert.Single(result.Rows);
        Assert.Equal(50.0, (double)result.Rows[0]["Total"]!);
    }

    // ── Combined predicates ────────────────────────────────────────────────

    [Fact]
    public void Combined_AndWithIsNull()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING, Active BOOL)");
        engine.Execute("INSERT INTO T (Id, Val, Active) VALUES (1, 'a', TRUE)");
        engine.Execute("INSERT INTO T (Id, Active) VALUES (2, TRUE)");
        engine.Execute("INSERT INTO T (Id, Val, Active) VALUES (3, 'c', FALSE)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val IS NULL AND Active = TRUE");

        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void Combined_OrWithBetween()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 5)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 15)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (3, 50)");

        var result = engine.Execute("SELECT Id FROM T WHERE Val BETWEEN 10 AND 25 OR Val > 40");

        Assert.Equal(2, result.Rows.Count);
    }

    // ── IS DISTINCT FROM ─────────────────────────────────────────────────────

    [Fact]
    public void IsDistinctFrom_DifferentValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        var result = engine.Execute("SELECT * FROM T WHERE Val IS DISTINCT FROM 10");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void IsNotDistinctFrom_SameValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");

        var result = engine.Execute("SELECT * FROM T WHERE Val IS NOT DISTINCT FROM 10");
        Assert.Single(result.Rows);
    }

    // ── CAST ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Cast_StringToInt()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, '42')");

        var result = engine.Execute("SELECT * FROM T WHERE CAST(Val AS INT) = 42");
        Assert.Single(result.Rows);
    }

    // ── Scalar functions ─────────────────────────────────────────────────────

    [Fact]
    public void Abs_Positive()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, -5)");

        var result = engine.Execute("SELECT ABS(Val) AS A FROM T");
        Assert.Equal(5.0, result.Rows[0]["A"]);
    }

    [Fact]
    public void Replace_Function()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");
        var result = engine.Execute("SELECT REPLACE('hello world', 'world', 'there') AS R FROM T");
        Assert.Equal("hello there", result.Rows[0]["R"]);
    }

    [Fact]
    public void CharIndex_Function()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");
        var result = engine.Execute("SELECT CHARINDEX('world', 'hello world') AS C FROM T");
        Assert.Equal(7L, result.Rows[0]["C"]);
    }

    [Fact]
    public void Coalesce_Function()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");
        var result = engine.Execute("SELECT COALESCE(NULL, NULL, 'found') AS C FROM T");
        Assert.Equal("found", result.Rows[0]["C"]);
    }

    // ── JSON functions / arrow operators ─────────────────────────────────────

    [Fact]
    public void JsonExtract_Function()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"name\": \"Alice\", \"age\": 30}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"name\": \"Bob\", \"age\": 25}')");

        var result = engine.Execute("SELECT Id FROM T WHERE JSON_EXTRACT(Data, '$.age') = 30");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonExtract_StringField()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"name\": \"Alice\"}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"name\": \"Bob\"}')");

        var result = engine.Execute("SELECT Id FROM T WHERE JSON_EXTRACT(Data, '$.name') = 'Alice'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonValue_Function()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"status\": \"active\"}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"status\": \"inactive\"}')");

        var result = engine.Execute("SELECT Id FROM T WHERE JSON_VALUE(Data, '$.status') = 'active'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonArrow_ExtractField()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"x\": 10}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"x\": 20}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data->'$.x' = 10");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonArrowUnquote_Field()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"tag\": \"hello\"}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"tag\": \"world\"}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data->>'$.tag' = 'hello'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonExtract_NestedPath()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"a\": {\"b\": {\"c\": 99}}}')");

        var result = engine.Execute("SELECT Id FROM T WHERE JSON_EXTRACT(Data, '$.a.b.c') = 99");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void JsonArrow_PostgresMemberKey()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"x\": 10}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"x\": 20}')");

        // Postgres-style single-key operand (no $. prefix).
        var result = engine.Execute("SELECT Id FROM T WHERE Data->'x' = 10");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonArrow_PostgresArrayIndex()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"tags\": [\"a\", \"b\", \"c\"]}')");

        // ->>0 navigates into nested array element via chained accessors.
        var result = engine.Execute("SELECT Id FROM T WHERE Data->'tags'->>1 = 'b'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonHashArrow_PathArray()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"a\": {\"b\": {\"c\": 99}}}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"a\": {\"b\": {\"c\": 1}}}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data#>'{a,b,c}' = 99");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonHashArrowUnquote_PathArrayText()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"a\": {\"name\": \"Alice\"}}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"a\": {\"name\": \"Bob\"}}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data#>>'{a,name}' = 'Alice'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    // ── B.4.3 Containment ───────────────────────────────────────────────────────

    [Fact]
    public void JsonContains_ObjectMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"x\": 10, \"y\": 20}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"x\": 10}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data @> '{\"x\": 10}'");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void JsonContains_ObjectNoMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"x\": 10}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data @> '{\"z\": 99}'");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void JsonContains_ArrayContains()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '[1, 2, 3]')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data @> '[2]'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonContainedBy_Match()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"x\": 10}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data <@ '{\"x\": 10, \"y\": 20}'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonContainedBy_NoMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"x\": 10, \"z\": 99}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data <@ '{\"x\": 10, \"y\": 20}'");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void JsonContains_NestedObject()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"a\": {\"b\": 1, \"c\": 2}}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"a\": {\"b\": 1}}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data @> '{\"a\": {\"b\": 1}}'");
        Assert.Equal(2, result.Rows.Count);
    }

    // ── B.4.3 Key-Existence ─────────────────────────────────────────────────────

    [Fact]
    public void JsonHasKey_Exists()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"name\": \"Alice\", \"age\": 30}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"name\": \"Bob\"}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data ? 'age'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonHasKey_NotExists()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"name\": \"Alice\"}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data ? 'missing'");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void JsonHasKey_NonObject()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '[1, 2, 3]')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data ? '0'");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void JsonHasAnyKey_Match()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"name\": \"Alice\", \"age\": 30}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"name\": \"Bob\"}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data ?| '[\"age\", \"city\"]'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonHasAnyKey_NoMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"name\": \"Alice\"}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data ?| '[\"city\", \"zip\"]'");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void JsonHasAllKeys_Match()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"name\": \"Alice\", \"age\": 30, \"city\": \"NYC\"}')");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (2, '{\"name\": \"Bob\", \"age\": 25}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data ?& '[\"name\", \"age\"]'");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void JsonHasAllKeys_NotAllMatch()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"name\": \"Alice\"}')");

        var result = engine.Execute("SELECT Id FROM T WHERE Data ?& '[\"name\", \"age\"]'");
        Assert.Empty(result.Rows);
    }

    // ── B.4.4 jsonb_* functions ─────────────────────────────────────────────────

    [Fact]
    public void JsonbBuildObject_StringAndNumber()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_BUILD_OBJECT('name', 'Alice', 'age', 42) AS Obj FROM T");
        Assert.Single(result.Rows);
        var obj = result.Rows[0]["Obj"]?.ToString();
        Assert.NotNull(obj);
        Assert.Contains("\"name\"", obj);
        Assert.Contains("\"Alice\"", obj);
        Assert.Contains("\"age\"", obj);
        Assert.Contains("42", obj);
    }

    [Fact]
    public void JsonbBuildObject_NumericValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_BUILD_OBJECT('x', 10, 'y', 20) AS Obj FROM T");
        var obj = result.Rows[0]["Obj"]?.ToString();
        Assert.NotNull(obj);
        Assert.Contains("10", obj);
        Assert.Contains("20", obj);
    }

    [Fact]
    public void JsonbBuildArray_MixedTypes()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_BUILD_ARRAY(1, 'two', 3.0) AS Arr FROM T");
        var arr = result.Rows[0]["Arr"]?.ToString();
        Assert.NotNull(arr);
        Assert.Contains("1", arr);
        Assert.Contains("two", arr);
    }

    [Fact]
    public void JsonbBuildArray_SingleValue()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_BUILD_ARRAY('hello') AS Arr FROM T");
        Assert.Contains("hello", result.Rows[0]["Arr"]?.ToString());
    }

    [Fact]
    public void JsonbStripNulls_RemovesNulls()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_STRIP_NULLS('{\"a\": 1, \"b\": null, \"c\": 2}') AS Obj FROM T");
        var obj = result.Rows[0]["Obj"]?.ToString();
        Assert.NotNull(obj);
        Assert.Contains("\"a\"", obj);
        Assert.Contains("\"c\"", obj);
        Assert.DoesNotContain("\"b\"", obj);
    }

    // ── B.4.4 Path-based functions ──────────────────────────────────────────────

    [Fact]
    public void JsonbSet_ReplaceTopLevelKey()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_SET('{\"a\": 1, \"b\": 2}', '$.b', 99) AS Obj FROM T");
        var obj = result.Rows[0]["Obj"]?.ToString();
        Assert.Contains("99", obj);
        Assert.Contains("\"a\"", obj);
    }

    [Fact]
    public void JsonbSet_NestedPath()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_SET('{\"a\": {\"b\": 1}}', '$.a.b', 42) AS Obj FROM T");
        var obj = result.Rows[0]["Obj"]?.ToString();
        Assert.Contains("42", obj);
    }

    [Fact]
    public void JsonbInsert_NewKey()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_INSERT('{\"a\": 1}', '$.b', 2) AS Obj FROM T");
        var obj = result.Rows[0]["Obj"]?.ToString();
        Assert.Contains("\"a\"", obj);
        Assert.Contains("\"b\"", obj);
    }

    [Fact]
    public void JsonbPathExists_KeyExists()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"a\": {\"b\": 1}}')");

        var result = engine.Execute("SELECT Id FROM T WHERE JSONB_PATH_EXISTS(Data, '$.a.b')");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void JsonbPathExists_KeyMissing()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("INSERT INTO T (Id, Data) VALUES (1, '{\"a\": 1}')");

        var result = engine.Execute("SELECT Id FROM T WHERE JSONB_PATH_EXISTS(Data, '$.missing')");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void JsonbPathQuery_SingleValue()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_PATH_QUERY('{\"a\": {\"b\": 99}}', '$.a.b') AS Val FROM T");
        Assert.Equal("99", result.Rows[0]["Val"]?.ToString());
    }

    [Fact]
    public void JsonbPathQuery_Wildcard()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (Id) VALUES (1)");

        var result = engine.Execute("SELECT JSONB_PATH_QUERY('[1, 2, 3]', '$[*]') AS Val FROM T");
        Assert.Equal("1", result.Rows[0]["Val"]?.ToString());
    }
}
