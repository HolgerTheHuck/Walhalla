using System.Linq;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// Parser-Tests für PLW (Walhalla Procedural Language).
/// Phase 1: Syntax wird erkannt, Body extrahiert und Richtungen korrekt geparst.
/// </summary>
public sealed class PlwParserTests
{
    [Fact]
    public void CreateProcedure_LanguagePlw_BeforeBody_IsRecognized()
    {
        var sql = """
            CREATE OR REPLACE PROCEDURE CalcSum(IN @a INT, IN @b INT, OUT @sum INT)
            LANGUAGE plw AS $$
            BEGIN
                sum := a + b;
            END;
            $$;
            """;

        var stmt = SqlStatementParser.Parse(sql) as SqlCreateProcedureStatement;
        Assert.NotNull(stmt);
        Assert.Equal("CalcSum", stmt.ProcedureName);
        Assert.Equal("plw", stmt.Language);
        Assert.Contains("sum := a + b;", stmt.Body);
    }

    [Fact]
    public void CreateProcedure_LanguagePlw_AfterBody_IsRecognized()
    {
        var sql = """
            CREATE PROCEDURE Echo(IN @msg STRING)
            AS $$ BEGIN RETURN msg; END; $$ LANGUAGE plw;
            """;

        var stmt = SqlStatementParser.Parse(sql) as SqlCreateProcedureStatement;
        Assert.NotNull(stmt);
        Assert.Equal("Echo", stmt.ProcedureName);
        Assert.Equal("plw", stmt.Language);
        Assert.Contains("RETURN msg;", stmt.Body);
    }

    [Fact]
    public void CreateProcedure_DollarTag_IsRecognized()
    {
        var sql = """
            CREATE PROCEDURE Tagged()
            LANGUAGE plw AS $body$
            BEGIN
                x := 1;
            END;
            $body$;
            """;

        var stmt = SqlStatementParser.Parse(sql) as SqlCreateProcedureStatement;
        Assert.NotNull(stmt);
        Assert.Equal("plw", stmt.Language);
        Assert.Contains("x := 1;", stmt.Body);
    }

    [Fact]
    public void CreateProcedure_ParameterDirections_AreParsed()
    {
        var sql = """
            CREATE PROCEDURE Directions(
                IN @pIn INT,
                OUT @pOut INT,
                INOUT @pInOut INT,
                @pDefault INT
            ) LANGUAGE plw AS $$ BEGIN NULL; END; $$;
            """;

        var stmt = SqlStatementParser.Parse(sql) as SqlCreateProcedureStatement;
        Assert.NotNull(stmt);
        Assert.Equal(4, stmt.Parameters.Count);

        Assert.Equal("@pIn", stmt.Parameters[0].Name);
        Assert.Equal(SqlParameterDirection.In, stmt.Parameters[0].Direction);
        Assert.False(stmt.Parameters[0].IsOutput);

        Assert.Equal("@pOut", stmt.Parameters[1].Name);
        Assert.Equal(SqlParameterDirection.Out, stmt.Parameters[1].Direction);
        Assert.True(stmt.Parameters[1].IsOutput);

        Assert.Equal("@pInOut", stmt.Parameters[2].Name);
        Assert.Equal(SqlParameterDirection.InOut, stmt.Parameters[2].Direction);
        Assert.True(stmt.Parameters[2].IsOutput);

        Assert.Equal("@pDefault", stmt.Parameters[3].Name);
        Assert.Equal(SqlParameterDirection.In, stmt.Parameters[3].Direction);
        Assert.False(stmt.Parameters[3].IsOutput);
    }

    [Fact]
    public void CreateProcedure_TrailingDirections_AreParsed()
    {
        var sql = """
            CREATE PROCEDURE Trailing(
                @a INT OUT,
                @b INT OUTPUT,
                @c INT INOUT,
                @d INT
            ) LANGUAGE plw AS $$ BEGIN NULL; END; $$;
            """;

        var stmt = SqlStatementParser.Parse(sql) as SqlCreateProcedureStatement;
        Assert.NotNull(stmt);
        Assert.Equal(4, stmt.Parameters.Count);
        Assert.Equal(SqlParameterDirection.Out, stmt.Parameters[0].Direction);
        Assert.Equal(SqlParameterDirection.Out, stmt.Parameters[1].Direction);
        Assert.Equal(SqlParameterDirection.InOut, stmt.Parameters[2].Direction);
        Assert.Equal(SqlParameterDirection.In, stmt.Parameters[3].Direction);
    }

    [Fact]
    public void CreateProcedure_DirectionAfterDefault_AreParsed()
    {
        var sql = """
            CREATE PROCEDURE WithDefaults(
                @a INT = 1 OUT,
                @b INT = 2 INOUT
            ) LANGUAGE plw AS $$ BEGIN NULL; END; $$;
            """;

        var stmt = SqlStatementParser.Parse(sql) as SqlCreateProcedureStatement;
        Assert.NotNull(stmt);
        Assert.Equal(2, stmt.Parameters.Count);
        Assert.Equal(SqlParameterDirection.Out, stmt.Parameters[0].Direction);
        Assert.Equal(1, stmt.Parameters[0].DefaultValue);
        Assert.Equal(SqlParameterDirection.InOut, stmt.Parameters[1].Direction);
        Assert.Equal(2, stmt.Parameters[1].DefaultValue);
    }

    [Fact]
    public void CreateProcedure_LegacyOutput_IsStillSupported()
    {
        var sql = """
            CREATE PROCEDURE LegacyOutput(@x INT OUTPUT)
            AS CSHARP BEGIN return; END;
            """;

        var stmt = SqlStatementParser.Parse(sql) as SqlCreateProcedureStatement;
        Assert.NotNull(stmt);
        Assert.Single(stmt.Parameters);
        Assert.Equal(SqlParameterDirection.Out, stmt.Parameters[0].Direction);
        Assert.True(stmt.Parameters[0].IsOutput);
        Assert.Equal("csharp", stmt.Language);
    }

    [Fact]
    public void CreateProcedure_PlwBody_Semicolons_ArePreserved()
    {
        var sql = """
            CREATE PROCEDURE Preserved()
            LANGUAGE plw AS $$
            DECLARE x INT := 0;
            BEGIN
                x := x + 1;
                y := x * 2;
            END;
            $$;
            """;

        var stmt = SqlStatementParser.Parse(sql) as SqlCreateProcedureStatement;
        Assert.NotNull(stmt);
        Assert.Equal("plw", stmt.Language);
        Assert.Contains("DECLARE x INT := 0;", stmt.Body);
        Assert.Contains("x := x + 1;", stmt.Body);
        Assert.Contains("y := x * 2;", stmt.Body);
    }
}
