using System.Linq;
using WalhallaSql.Parsing.Plw;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// Tests für den PLW-Parser / AST (Phase 3).
/// </summary>
public sealed class PlwAstTests
{
    private static PlwProgram Parse(string source)
    {
        var tokens = PlwTokenizer.Tokenize(source);
        return PlwParser.Parse(tokens);
    }

    [Fact]
    public void Parse_SimpleBlock_ReturnsProgram()
    {
        var program = Parse("""
            BEGIN
                x := 1;
            END;
            """);

        Assert.NotNull(program);
        Assert.Empty(program.Parameters);
        Assert.IsType<PlwBlock>(program.Body);
    }

    [Fact]
    public void Parse_Declaration_ReturnsVariableDeclaration()
    {
        var program = Parse("""
            DECLARE
                x INT := 0;
            BEGIN
                NULL;
            END;
            """);

        var block = Assert.IsType<PlwBlock>(program.Body);
        Assert.Single(block.Declarations);
        var decl = block.Declarations[0];
        Assert.Equal("x", decl.Name);
        Assert.Equal("INT", decl.TypeName);
        Assert.NotNull(decl.DefaultValue);
    }

    [Fact]
    public void Parse_Assignment_ReturnsAssignmentNode()
    {
        var program = Parse("BEGIN x := y + 1; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var assign = Assert.IsType<PlwAssignment>(block.Body[0]);
        var target = Assert.IsType<PlwIdentifierExpression>(assign.Target);
        Assert.Equal("x", target.Name);
        Assert.IsType<PlwBinaryExpression>(assign.Value);
    }

    [Fact]
    public void Parse_If_Then_Else_ReturnsIfNode()
    {
        var program = Parse("""
            BEGIN
                IF a > 0 THEN
                    x := 1;
                ELSE
                    x := 0;
                END IF;
            END;
            """);

        var block = Assert.IsType<PlwBlock>(program.Body);
        var ifStmt = Assert.IsType<PlwIf>(block.Body[0]);
        Assert.NotNull(ifStmt.Condition);
        Assert.NotNull(ifStmt.ThenBranch);
        Assert.NotNull(ifStmt.ElseBranch);
    }

    [Fact]
    public void Parse_Elsif_ReturnsElsifBranches()
    {
        var program = Parse("""
            BEGIN
                IF a > 0 THEN
                    x := 1;
                ELSIF a = 0 THEN
                    x := 0;
                ELSE
                    x := -1;
                END IF;
            END;
            """);

        var ifStmt = Assert.IsType<PlwIf>(((PlwBlock)program.Body).Body[0]);
        Assert.Single(ifStmt.ElsifBranches);
        Assert.NotNull(ifStmt.ElseBranch);
    }

    [Fact]
    public void Parse_WhileLoop_ReturnsWhileLoop()
    {
        var program = Parse("BEGIN WHILE i < 10 LOOP i := i + 1; END LOOP; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var loop = Assert.IsType<PlwWhileLoop>(block.Body[0]);
        Assert.NotNull(loop.Condition);
        Assert.NotNull(loop.Body);
    }

    [Fact]
    public void Parse_ForIntegerLoop_ReturnsForIntegerLoop()
    {
        var program = Parse("BEGIN FOR i IN 1..10 LOOP x := x + 1; END LOOP; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var loop = Assert.IsType<PlwForIntegerLoop>(block.Body[0]);
        Assert.Equal("i", loop.VariableName);
        Assert.IsType<PlwNumberExpression>(loop.Lower);
        Assert.IsType<PlwNumberExpression>(loop.Upper);
        Assert.False(loop.Reverse);
    }

    [Fact]
    public void Parse_ForQueryLoop_ReturnsForQueryLoop()
    {
        var program = Parse("BEGIN FOR rec IN SELECT Id FROM T LOOP x := rec.Id; END LOOP; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var loop = Assert.IsType<PlwForQueryLoop>(block.Body[0]);
        Assert.Equal("rec", loop.VariableName);
        Assert.NotNull(loop.Query);
        Assert.NotNull(loop.Body);
    }

    [Fact]
    public void Parse_ReturnQuery_ReturnsReturnQuery()
    {
        var program = Parse("BEGIN RETURN QUERY SELECT Id, Name FROM T; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var ret = Assert.IsType<PlwReturnQuery>(block.Body[0]);
        Assert.Contains("SELECT Id, Name FROM T", ret.Query.Text);
    }

    [Fact]
    public void Parse_Perform_ReturnsPerformNode()
    {
        var program = Parse("BEGIN PERFORM other_proc(); END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var perform = Assert.IsType<PlwPerform>(block.Body[0]);
        Assert.NotNull(perform.Statement);
    }

    [Fact]
    public void Parse_ExecuteUsing_ReturnsExecuteNode()
    {
        var program = Parse("BEGIN EXECUTE 'SELECT * FROM T WHERE Id = $1' USING id; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var execute = Assert.IsType<PlwExecute>(block.Body[0]);
        Assert.NotNull(execute.SqlExpression);
        Assert.Single(execute.UsingArguments);
    }

    [Fact]
    public void Parse_Raise_Notice_ReturnsRaiseNode()
    {
        var program = Parse("BEGIN RAISE NOTICE 'Value: %', x; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var raise = Assert.IsType<PlwRaise>(block.Body[0]);
        Assert.Equal("NOTICE", raise.Level);
        Assert.NotNull(raise.Message);
        Assert.Single(raise.Arguments);
    }

    [Fact]
    public void Parse_SelectInto_ReturnsSelectIntoNode()
    {
        var program = Parse("BEGIN SELECT Name INTO v_name FROM Customers WHERE Id = 1; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var selectInto = Assert.IsType<PlwSelectInto>(block.Body[0]);
        Assert.Single(selectInto.Targets);
        Assert.Contains("FROM Customers", selectInto.SelectSql.Text);
        Assert.DoesNotContain("INTO", selectInto.SelectSql.Text);
    }

    [Fact]
    public void Parse_NestedBlock_ReturnsNestedBlock()
    {
        var program = Parse("""
            BEGIN
                BEGIN
                    x := 1;
                END;
            END;
            """);

        var outer = Assert.IsType<PlwBlock>(program.Body);
        Assert.IsType<PlwBlock>(outer.Body[0]);
    }

    [Fact]
    public void Parse_ParameterList_ReturnsParameters()
    {
        var program = Parse("(IN @a INT, OUT @b STRING) BEGIN b := 'x'; END;");

        Assert.Equal(2, program.Parameters.Count);
        Assert.Equal("@a", program.Parameters[0].Name);
        Assert.Equal("INT", program.Parameters[0].TypeName);
        Assert.True(program.Parameters[0].IsParameter);
        Assert.Equal("@b", program.Parameters[1].Name);
        Assert.Equal("STRING", program.Parameters[1].TypeName);
    }

    [Fact]
    public void Parse_SqlStatement_ReturnsSqlStatementNode()
    {
        var program = Parse("BEGIN INSERT INTO Log (Id) VALUES (1); END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var sql = Assert.IsType<PlwSqlStatement>(block.Body[0]);
        Assert.Contains("INSERT INTO Log", sql.Sql.Text);
    }

    [Fact]
    public void Parse_BooleanAndNullConstants()
    {
        var program = Parse("BEGIN x := TRUE; y := FALSE; z := NULL; END;");

        var block = Assert.IsType<PlwBlock>(program.Body);
        var first = Assert.IsType<PlwAssignment>(block.Body[0]);
        Assert.IsType<PlwBooleanExpression>(first.Value);

        var second = Assert.IsType<PlwAssignment>(block.Body[1]);
        Assert.IsType<PlwBooleanExpression>(second.Value);

        var third = Assert.IsType<PlwAssignment>(block.Body[2]);
        Assert.IsType<PlwNullExpression>(third.Value);
    }

}
