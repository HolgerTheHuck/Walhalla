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

    [Fact]
    public async Task ExtendedQuery_ParameterizedSelect_ReusesStatementAcrossExecutions()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE PkItems (Id INT, Value INT)");
        await Execute(conn, "INSERT INTO PkItems (Id, Value) VALUES (1, 100)");
        await Execute(conn, "INSERT INTO PkItems (Id, Value) VALUES (2, 200)");
        await Execute(conn, "INSERT INTO PkItems (Id, Value) VALUES (3, 300)");

        // Npgsql uses the extended query protocol automatically as soon as parameters
        // are present. This exercises the engine-side prepared-statement cache
        // (Parse/Bind/Execute) instead of the literal-rewrite fallback.
        await using var selectCmd = new NpgsqlCommand(
            "SELECT Value FROM PkItems WHERE Id = @id", conn);
        selectCmd.Parameters.AddWithValue("id", 2);

        var first = await selectCmd.ExecuteScalarAsync();
        Assert.Equal("200", first?.ToString());

        selectCmd.Parameters["id"].Value = 3;
        var second = await selectCmd.ExecuteScalarAsync();
        Assert.Equal("300", second?.ToString());

        // Range query to ensure the compiled PK-range path is also covered.
        await using var rangeCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM PkItems WHERE Id >= @min AND Id <= @max", conn);
        rangeCmd.Parameters.AddWithValue("min", 1);
        rangeCmd.Parameters.AddWithValue("max", 3);

        var count = await rangeCmd.ExecuteScalarAsync();
        Assert.Equal("3", count?.ToString());
    }

    [Fact]
    public async Task ExtendedQuery_ParameterizedInsert_ReusesStatementAcrossExecutions()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE BatchItems (Id INT, Name VARCHAR(100))");

        // Npgsql verwendet das Extended-Query-Protokoll; ein parametrisiertes INSERT
        // wird als Parse/Bind/Execute ausgeführt und sollte das vorbereitete DML-
        // Statement wiederverwenden.
        await using var insertCmd = new NpgsqlCommand(
            "INSERT INTO BatchItems (Id, Name) VALUES (@id, @name)", conn);
        insertCmd.Parameters.AddWithValue("id", 1);
        insertCmd.Parameters.AddWithValue("name", "alpha");
        Assert.Equal(1, await insertCmd.ExecuteNonQueryAsync());

        insertCmd.Parameters["id"].Value = 2;
        insertCmd.Parameters["name"].Value = "beta";
        Assert.Equal(1, await insertCmd.ExecuteNonQueryAsync());

        await using var selectCmd = new NpgsqlCommand(
            "SELECT Id, Name FROM BatchItems ORDER BY Id", conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("alpha", reader.GetString(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("beta", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task VirtualCatalog_InformationSchema_RoutinesAndParameters_ListsProcedure()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE Orders (Id INT)");
        await Execute(conn, "CREATE PROCEDURE GetOrderCount(@minId INT) AS SELECT COUNT(*) FROM Orders WHERE Id >= @minId");

        var routines = new List<(string Name, string Type)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT routine_name, routine_type FROM information_schema.routines WHERE routine_schema = 'public'", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                routines.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Contains(routines, r => r.Name == "GetOrderCount");

        var parameters = new List<(string RoutineName, string ParameterName, string DataType)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT specific_name, parameter_name, data_type FROM information_schema.parameters", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                parameters.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        Assert.Contains(parameters, p => p.RoutineName == "GetOrderCount" && p.ParameterName == "minId");
    }

    private static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
