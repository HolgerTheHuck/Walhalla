using System;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// End-to-End-Tests für C#-Stored-Procedures (Language="csharp", Roslyn-kompiliert)
/// gegen WalhallaSql. Spiegelt die Suite aus LayeredSql.EfCore.Tests.
/// </summary>
public sealed class CSharpStoredProcedureTests
{
    [Fact]
    public void CSharp_SP_executes_and_returns_ok()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE DoNothing()
            AS CSHARP BEGIN
                // intentionally empty
            END
            """);

        // Sollte nicht werfen.
        engine.Execute("EXEC DoNothing");
    }

    [Fact]
    public void CSharp_SP_receives_int_parameter_and_inserts_row()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Log (Id INT PRIMARY KEY, Message STRING)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE WriteLog(@id INT, @msg STRING)
            AS CSHARP BEGIN
                ctx.Execute($"INSERT INTO Log (Id, Message) VALUES ({id}, '{msg}')");
            END
            """);

        engine.Execute("EXEC WriteLog @id = 42, @msg = 'hello from csharp'");

        var result = engine.Execute("SELECT Message FROM Log WHERE Id = 42");
        Assert.Single(result.Rows);
        Assert.Equal("hello from csharp", result.Rows[0]["Message"]);
    }

    [Fact]
    public void CSharp_SP_queries_and_returns_result_set()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY, Name STRING, Value INT)");
        engine.Execute("INSERT INTO Items (Id, Name, Value) VALUES (1, 'Alpha', 10)");
        engine.Execute("INSERT INTO Items (Id, Name, Value) VALUES (2, 'Beta', 20)");
        engine.Execute("INSERT INTO Items (Id, Name, Value) VALUES (3, 'Gamma', 5)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE GetItemsAbove(@minValue INT)
            AS CSHARP BEGIN
                var rows = ctx.Query($"SELECT Id, Name FROM Items WHERE Value >= {minValue} ORDER BY Id");
                return WalhallaResultSet.FromRows(rows);
            END
            """);

        var result = engine.Execute("EXEC GetItemsAbove @minValue = 10");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Alpha", result.Rows[0]["Name"]);
        Assert.Equal("Beta", result.Rows[1]["Name"]);
    }

    [Fact]
    public void CSharp_SP_or_replace_recompiles_on_next_exec()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Counter (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO Counter (Id, Val) VALUES (1, 0)");

        // Erste Version: setzt Val auf 99
        engine.Execute("""
            CREATE OR REPLACE PROCEDURE UpdateCounter()
            AS CSHARP BEGIN
                ctx.Execute("UPDATE Counter SET Val = 99 WHERE Id = 1");
            END
            """);

        engine.Execute("EXEC UpdateCounter");

        // Zweite Version via OR REPLACE: setzt Val auf 77
        engine.Execute("""
            CREATE OR REPLACE PROCEDURE UpdateCounter()
            AS CSHARP BEGIN
                ctx.Execute("UPDATE Counter SET Val = 77 WHERE Id = 1");
            END
            """);

        engine.Execute("EXEC UpdateCounter");

        var result = engine.Execute("SELECT Val FROM Counter WHERE Id = 1");
        Assert.Single(result.Rows);
        Assert.Equal(77, Convert.ToInt32(result.Rows[0]["Val"]));
    }

    [Fact]
    public void CSharp_SP_compilation_error_throws_descriptive_exception()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE BrokenProc()
            AS CSHARP BEGIN
                this_does_not_compile_at_all(((;
            END
            """);

        var ex = Assert.ThrowsAny<Exception>(() => engine.Execute("EXEC BrokenProc"));

        // Die Fehlermeldung muss den Prozedurnamen enthalten
        Assert.Contains("BrokenProc", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CSharp_SP_nullable_parameter_defaults_to_null()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Results (Id INT PRIMARY KEY, Flag BIT)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE InsertFlag(@id INT, @flag BIT = NULL)
            AS CSHARP BEGIN
                var flagVal = flag.HasValue ? (flag.Value ? "true" : "false") : "NULL";
                ctx.Execute($"INSERT INTO Results (Id, Flag) VALUES ({id}, {flagVal})");
            END
            """);

        // Mit explizitem Wert
        engine.Execute("EXEC InsertFlag @id = 1, @flag = TRUE");
        // Mit NULL (Default)
        engine.Execute("EXEC InsertFlag @id = 2");

        var result = engine.Execute("SELECT Flag FROM Results WHERE Id = 2");
        Assert.Single(result.Rows);
        Assert.Null(result.Rows[0]["Flag"]);
    }
}
