using WalhallaSql.AdoNet.SqlClient;

namespace WalhallaSql.EfCore.Tests;

/// <summary>
/// Regressionstests für SqlLiteralFormatter. Diese sollen sicherstellen,
/// dass SQL-Parameter und Prozedurargumente korrekt unterschieden werden,
/// ohne die riesige EF-Spec-Suite laufen lassen zu müssen.
/// </summary>
public sealed class SqlLiteralFormatterTests
{
    [Fact]
    public void Named_exec_argument_is_not_treated_as_missing_parameter()
    {
        var command = new SqlClientCommand(
            "EXEC WriteLog @id = 42, @msg = 'hello from csharp'")
        {
            Parameters = []
        };

        var rewritten = SqlLiteralFormatter.RewriteParametersAsLiterals(command);

        Assert.Equal("EXEC WriteLog @id = 42, @msg = 'hello from csharp'", rewritten);
    }

    [Fact]
    public void Positional_exec_argument_is_not_treated_as_missing_parameter()
    {
        var command = new SqlClientCommand(
            "EXEC WriteLog 42, 'hello'")
        {
            Parameters = []
        };

        var rewritten = SqlLiteralFormatter.RewriteParametersAsLiterals(command);

        Assert.Equal("EXEC WriteLog 42, 'hello'", rewritten);
    }

    [Fact]
    public void Regular_sql_parameter_is_still_replaced()
    {
        var command = new SqlClientCommand(
            "SELECT * FROM Users WHERE Id = @id AND Name = @name")
        {
            Parameters = [new SqlClientParameter("@id", 42), new SqlClientParameter("@name", "Alice")]
        };

        var rewritten = SqlLiteralFormatter.RewriteParametersAsLiterals(command);

        Assert.Equal("SELECT * FROM Users WHERE Id = 42 AND Name = 'Alice'", rewritten);
    }

    [Fact]
    public void Missing_regular_parameter_still_throws()
    {
        var command = new SqlClientCommand(
            "SELECT * FROM Users WHERE Id = @id")
        {
            Parameters = []
        };

        Assert.Throws<InvalidOperationException>(() => SqlLiteralFormatter.RewriteParametersAsLiterals(command));
    }
}
