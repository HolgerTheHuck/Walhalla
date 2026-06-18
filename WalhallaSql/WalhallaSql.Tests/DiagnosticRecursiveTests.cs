using System.IO;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class DiagnosticRecursiveTests
{
    [Fact]
    public void Parse_RecursiveCte_MultiLine_Structure()
    {
        var sql = @"
            WITH RECURSIVE cte AS (
                SELECT N FROM T
                UNION ALL
                SELECT N+1 FROM cte WHERE N < 3
            )
            SELECT * FROM cte";

        var stmt = SqlStatementParser.Parse(sql);

        Assert.IsType<SqlWithStatement>(stmt);
        var withStmt = (SqlWithStatement)stmt;
        Assert.True(withStmt.IsRecursive);
        Assert.Single(withStmt.Ctes);

        var body = withStmt.Ctes[0].Body;
        Assert.IsType<SqlCompoundSelectStatement>(body);
        var compound = (SqlCompoundSelectStatement)body;
        Assert.Equal(SqlSetOperator.UnionAll, compound.Operator);

        // Anchor should be a simple SELECT
        Assert.IsType<SqlSelectStatement>(compound.Left);
        var anchor = (SqlSelectStatement)compound.Left;
        Assert.Single(anchor.Columns);
        Assert.Equal("N", anchor.Columns[0].Expression);
    }

    [Fact]
    public void Execute_RecursiveCte_SingleLine_Works()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (N INT PRIMARY KEY)");
        engine.Execute("INSERT INTO T (N) VALUES (1)");

        var result = engine.Execute(
            "WITH RECURSIVE cte AS (SELECT N FROM T UNION ALL SELECT N+1 FROM cte WHERE N < 3) SELECT * FROM cte");

        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void Execute_RecursiveCte_MultiLine_Works()
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
}
