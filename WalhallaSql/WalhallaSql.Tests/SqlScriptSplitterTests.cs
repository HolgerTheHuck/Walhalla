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
    public void CreateProcedure_FollowedByInsert_TwoStatements()
    {
        var sql = @"
            CREATE PROCEDURE P AS BEGIN INSERT INTO T (Id) VALUES (1); END;
            INSERT INTO T (Id) VALUES (2);";

        var result = SqlScriptSplitter.Split(sql);
        Assert.Equal(2, result.Count);
        Assert.StartsWith("CREATE PROCEDURE P", result[0]);
        Assert.Equal("INSERT INTO T (Id) VALUES (2)", result[1]);
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
}
