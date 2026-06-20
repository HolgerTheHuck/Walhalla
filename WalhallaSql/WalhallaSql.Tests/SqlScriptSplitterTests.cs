using System;
using System.Linq;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class SqlScriptSplitterTests
{
    [Fact]
    public void Empty_ReturnsEmpty()
    {
        var result = SqlScriptSplitter.Split("   ");
        Assert.Empty(result);
    }

    [Fact]
    public void SingleStatement_NoSemicolon()
    {
        var result = SqlScriptSplitter.Split("SELECT 1");
        Assert.Single(result);
        Assert.Equal("SELECT 1", result[0]);
    }

    [Fact]
    public void SingleStatement_TrailingSemicolon()
    {
        var result = SqlScriptSplitter.Split("SELECT 1;");
        Assert.Single(result);
        Assert.Equal("SELECT 1", result[0]);
    }

    [Fact]
    public void TwoStatements_BySemicolon()
    {
        var result = SqlScriptSplitter.Split("SELECT 1; SELECT 2");
        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT 1", result[0]);
        Assert.Equal("SELECT 2", result[1]);
    }

    [Fact]
    public void SemicolonInString_NotSplit()
    {
        var result = SqlScriptSplitter.Split("SELECT 'a;b'");
        Assert.Single(result);
        Assert.Equal("SELECT 'a;b'", result[0]);
    }

    [Fact]
    public void LineComment_Removed()
    {
        var result = SqlScriptSplitter.Split("-- comment\nSELECT 1");
        Assert.Single(result);
        Assert.Equal("SELECT 1", result[0]);
    }

    [Fact]
    public void BlockComment_Removed()
    {
        var result = SqlScriptSplitter.Split("/* multi\nline */ SELECT 2");
        Assert.Single(result);
        Assert.Equal("SELECT 2", result[0]);
    }

    [Fact]
    public void CreateProcedure_WithInternalSemicolons_IsSingleStatement()
    {
        var sql = @"
            CREATE PROCEDURE P
            AS
            BEGIN
                INSERT INTO T (Id) VALUES (1);
                INSERT INTO T (Id) VALUES (2);
            END";

        var result = SqlScriptSplitter.Split(sql);
        Assert.Single(result);
        Assert.Contains("CREATE PROCEDURE P", result[0]);
        Assert.Contains("INSERT INTO T (Id) VALUES (2);", result[0]);
    }

    [Fact]
    public void CSharpProcedure_WithSemicolonInVerbatimString_IsSingleStatement()
    {
        var lines = new[]
        {
            "CREATE OR REPLACE PROCEDURE GetCustomerTotalSpend(@customerId INT)",
            "AS CSHARP BEGIN",
            "    var rows = ctx.Query($@\"\"\"",
            "        SELECT c.Name, COALESCE(SUM(oi.Quantity * p.Price), 0) AS TotalSpend",
            "        FROM Customers c",
            "        LEFT JOIN Orders o ON c.Id = o.CustomerId",
            "        WHERE c.Id = {customerId}",
            "        GROUP BY c.Id, c.Name\"\"\");",
            "    return WalhallaResultSet.FromRows(rows);",
            "END;"
        };
        var sql = string.Join("\n", lines);

        var result = SqlScriptSplitter.Split(sql);
        Assert.Single(result);
        Assert.Contains("TotalSpend", result[0]);
        Assert.Contains("GROUP BY c.Id, c.Name", result[0]);
    }

    [Fact]
    public void CommentsAndMultipleStatements_Mixed()
    {
        var sql = @"
            -- setup
            CREATE TABLE T (Id INT PRIMARY KEY);
            /* insert data */
            INSERT INTO T (Id) VALUES (1);
            SELECT * FROM T -- final select
        ";

        var result = SqlScriptSplitter.Split(sql);
        Assert.Equal(3, result.Count);
        Assert.Equal("CREATE TABLE T (Id INT PRIMARY KEY)", result[0]);
        Assert.Equal("INSERT INTO T (Id) VALUES (1)", result[1]);
        Assert.Equal("SELECT * FROM T", result[2]);
    }

    [Fact]
    public void GetStatementAtOffset_SingleStatement_ReturnsIt()
    {
        var sql = "SELECT 1";
        Assert.Equal("SELECT 1", SqlScriptSplitter.GetStatementAtOffset(sql, 0));
        Assert.Equal("SELECT 1", SqlScriptSplitter.GetStatementAtOffset(sql, 3));
        Assert.Equal("SELECT 1", SqlScriptSplitter.GetStatementAtOffset(sql, sql.Length));
    }

    [Fact]
    public void GetStatementAtOffset_FirstOfTwoStatements()
    {
        var sql = "SELECT 1; SELECT 2";
        Assert.Equal("SELECT 1", SqlScriptSplitter.GetStatementAtOffset(sql, 0));
        Assert.Equal("SELECT 1", SqlScriptSplitter.GetStatementAtOffset(sql, 7));
        Assert.Equal("SELECT 1", SqlScriptSplitter.GetStatementAtOffset(sql, 8)); // auf Semikolon
    }

    [Fact]
    public void GetStatementAtOffset_SecondOfTwoStatements()
    {
        var sql = "SELECT 1; SELECT 2";
        Assert.Equal("SELECT 2", SqlScriptSplitter.GetStatementAtOffset(sql, 10));
        Assert.Equal("SELECT 2", SqlScriptSplitter.GetStatementAtOffset(sql, 18));
    }

    [Fact]
    public void GetStatementAtOffset_BetweenStatements_ReturnsNext()
    {
        var sql = "SELECT 1;\n\n   SELECT 2";
        Assert.Equal("SELECT 2", SqlScriptSplitter.GetStatementAtOffset(sql, 11));
    }

    [Fact]
    public void GetStatementAtOffset_SemicolonInString_NotSplit()
    {
        var sql = "SELECT 'a;b'; SELECT 2";
        Assert.Equal("SELECT 'a;b'", SqlScriptSplitter.GetStatementAtOffset(sql, 5));
        Assert.Equal("SELECT 2", SqlScriptSplitter.GetStatementAtOffset(sql, 16));
    }

    [Fact]
    public void GetStatementAtOffset_CreateBlock_ReturnsWholeBlock()
    {
        var sql = @"
            CREATE TRIGGER trg
            ON T AFTER INSERT AS
            BEGIN
                INSERT INTO AuditLog (Id) SELECT INSERTED.Id FROM INSERTED;
            END;
            SELECT 1";

        var blockStart = sql.IndexOf("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase);
        var blockEnd = sql.IndexOf("END;", StringComparison.OrdinalIgnoreCase) + 3;
        var afterBlock = sql.IndexOf("SELECT 1", StringComparison.OrdinalIgnoreCase);

        var result = SqlScriptSplitter.GetStatementAtOffset(sql, blockStart + 5);
        Assert.Contains("CREATE TRIGGER trg", result);
        Assert.Contains("INSERTED", result);

        Assert.Equal("SELECT 1", SqlScriptSplitter.GetStatementAtOffset(sql, afterBlock));
    }

    [Fact]
    public void GetStatementAtOffset_CursorAfterLastStatement_ReturnsLast()
    {
        var sql = "SELECT 1; SELECT 2";
        Assert.Equal("SELECT 2", SqlScriptSplitter.GetStatementAtOffset(sql, sql.Length + 100));
    }
}
