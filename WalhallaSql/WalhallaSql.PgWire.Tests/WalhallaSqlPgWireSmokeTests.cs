using Npgsql;
using Xunit;

namespace WalhallaSql.PgWire.Tests;

/// <summary>
/// Smoke tests verifying that <c>WalhallaSqlPgWireBackend</c> exposes
/// <see cref="WalhallaEngine"/> through the PgWire frontend end-to-end via
/// Npgsql. These mirror the most basic <c>LayeredSql.PgWire.Tests</c> scenarios.
/// </summary>
public class WalhallaSqlPgWireSmokeTests
{
    [Fact]
    public async Task SimpleQuery_CreateTable_DoesNotThrow()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await using var cmd = new NpgsqlCommand(
            "CREATE TABLE Products (Id INT, Name VARCHAR(100))", conn);

        var rowsAffected = await cmd.ExecuteNonQueryAsync();
        Assert.Equal(-1, rowsAffected);
    }

    [Fact]
    public async Task SimpleQuery_InsertAndSelect_ReturnsInsertedRow()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE Customers (Id INT, Name VARCHAR(200))");
        await Execute(conn, "INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");

        await using var selectCmd = new NpgsqlCommand("SELECT Id, Name FROM Customers", conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "Expected at least one row.");
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Alice", reader.GetString(1));
        Assert.False(await reader.ReadAsync(), "Expected exactly one row.");
    }

    [Fact]
    public async Task SimpleQuery_UpdateAndSelect_ReturnsUpdatedValue()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE Items (Id INT, Value INT)");
        await Execute(conn, "INSERT INTO Items (Id, Value) VALUES (1, 10)");
        await Execute(conn, "UPDATE Items SET Value = 99 WHERE Id = 1");

        await using var cmd = new NpgsqlCommand("SELECT Value FROM Items WHERE Id = 1", conn);
        var result = await cmd.ExecuteScalarAsync();

        Assert.Equal("99", result?.ToString());
    }

    [Fact]
    public async Task SimpleQuery_DeleteAndSelect_RowGone()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE Log (Id INT, Msg VARCHAR(100))");
        await Execute(conn, "INSERT INTO Log (Id, Msg) VALUES (1, 'hello')");
        await Execute(conn, "DELETE FROM Log WHERE Id = 1");

        await using var cmd = new NpgsqlCommand("SELECT Id FROM Log", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.False(await reader.ReadAsync(), "Expected no rows after DELETE.");
    }

    private static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
