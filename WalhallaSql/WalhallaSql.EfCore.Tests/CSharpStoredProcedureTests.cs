using System;
using System.Data;
using WalhallaSql;
using WalhallaSql.AdoNet;

namespace WalhallaSql.EfCore.Tests;

/// <summary>
/// End-to-End-Tests für C#-Stored-Procedures (Language="csharp", Roslyn-kompiliert).
/// Jeder Test verwendet eine isolierte :memory:-Datenbank.
/// </summary>
public sealed class CSharpStoredProcedureTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static WalhallaSqlDbConnection OpenConnection()
    {
        var conn = new WalhallaSqlDbConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private static void Exec(WalhallaSqlDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public void CSharp_SP_executes_and_returns_ok()
    {
        // Ein minimaler C#-SP-Body, der nichts tut und mit dem impliziten OK-Result endet.
        using var conn = OpenConnection();

        Exec(conn, """
            CREATE OR REPLACE PROCEDURE DoNothing()
            AS CSHARP BEGIN
                // intentionally empty
            END
            """);

        // Sollte nicht werfen.
        Exec(conn, "EXEC DoNothing");
    }

    [Fact]
    public void CSharp_SP_receives_int_parameter_and_inserts_row()
    {
        using var conn = OpenConnection();

        Exec(conn, "CREATE TABLE Log (Id INT PRIMARY KEY, Message VARCHAR(200))");

        Exec(conn, """
            CREATE OR REPLACE PROCEDURE WriteLog(@id INT, @msg NVARCHAR(500))
            AS CSHARP BEGIN
                ctx.Execute($"INSERT INTO Log (Id, Message) VALUES ({id}, '{msg}')");
            END
            """);

        Exec(conn, "EXEC WriteLog @id = 42, @msg = 'hello from csharp'");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Message FROM Log WHERE Id = 42";
        var result = cmd.ExecuteScalar()?.ToString();

        Assert.Equal("hello from csharp", result);
    }

    [Fact]
    public void CSharp_SP_queries_and_returns_result_set()
    {
        using var conn = OpenConnection();

        Exec(conn, "CREATE TABLE Items (Id INT PRIMARY KEY, Name VARCHAR(200), Value INT)");
        Exec(conn, "INSERT INTO Items (Id, Name, Value) VALUES (1, 'Alpha', 10)");
        Exec(conn, "INSERT INTO Items (Id, Name, Value) VALUES (2, 'Beta', 20)");
        Exec(conn, "INSERT INTO Items (Id, Name, Value) VALUES (3, 'Gamma', 5)");

        Exec(conn, """
            CREATE OR REPLACE PROCEDURE GetItemsAbove(@minValue INT)
            AS CSHARP BEGIN
                var rows = ctx.Query($"SELECT Id, Name FROM Items WHERE Value >= {minValue} ORDER BY Id");
                return WalhallaResultSet.FromRows(rows);
            END
            """);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXEC GetItemsAbove @minValue = 10";
        using var reader = cmd.ExecuteReader();

        var names = new System.Collections.Generic.List<string>();
        while (reader.Read())
            names.Add(reader.GetString(reader.GetOrdinal("Name")));

        Assert.Equal(["Alpha", "Beta"], names);
    }

    [Fact]
    public void CSharp_SP_or_replace_recompiles_on_next_exec()
    {
        using var conn = OpenConnection();
        Exec(conn, "CREATE TABLE Counter (Id INT PRIMARY KEY, Val INT)");
        Exec(conn, "INSERT INTO Counter (Id, Val) VALUES (1, 0)");

        // Erste Version: setzt Val auf 99
        Exec(conn, """
            CREATE OR REPLACE PROCEDURE UpdateCounter()
            AS CSHARP BEGIN
                ctx.Execute("UPDATE Counter SET Val = 99 WHERE Id = 1");
            END
            """);

        Exec(conn, "EXEC UpdateCounter");

        // Zweite Version via OR REPLACE: setzt Val auf 77
        Exec(conn, """
            CREATE OR REPLACE PROCEDURE UpdateCounter()
            AS CSHARP BEGIN
                ctx.Execute("UPDATE Counter SET Val = 77 WHERE Id = 1");
            END
            """);

        Exec(conn, "EXEC UpdateCounter");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Val FROM Counter WHERE Id = 1";
        var val = Convert.ToInt32(cmd.ExecuteScalar());

        Assert.Equal(77, val);
    }

    [Fact]
    public void CSharp_SP_compilation_error_throws_descriptive_exception()
    {
        using var conn = OpenConnection();

        Exec(conn, """
            CREATE OR REPLACE PROCEDURE BrokenProc()
            AS CSHARP BEGIN
                this_does_not_compile_at_all(((;
            END
            """);

        var ex = Assert.ThrowsAny<Exception>(() => Exec(conn, "EXEC BrokenProc"));

        // Die Fehlermeldung muss den Prozedurnamen enthalten
        Assert.Contains("BrokenProc", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CSharp_SP_nullable_parameter_defaults_to_null()
    {
        using var conn = OpenConnection();
        Exec(conn, "CREATE TABLE Results (Id INT PRIMARY KEY, Flag BIT)");

        Exec(conn, """
            CREATE OR REPLACE PROCEDURE InsertFlag(@id INT, @flag BIT = NULL)
            AS CSHARP BEGIN
                var flagVal = flag.HasValue ? (flag.Value ? "1" : "0") : "NULL";
                ctx.Execute($"INSERT INTO Results (Id, Flag) VALUES ({id}, {flagVal})");
            END
            """);

        // Mit explizitem Wert
        Exec(conn, "EXEC InsertFlag @id = 1, @flag = 1");
        // Mit NULL (Default)
        Exec(conn, "EXEC InsertFlag @id = 2");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Flag FROM Results WHERE Id = 2";
        var val = cmd.ExecuteScalar();

        Assert.True(val == null || val == DBNull.Value);
    }
}
