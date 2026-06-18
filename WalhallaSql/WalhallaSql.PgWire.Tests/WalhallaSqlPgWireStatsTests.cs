using Npgsql;
using Xunit;

namespace WalhallaSql.PgWire.Tests;

/// <summary>
/// Verifies that the <c>pg_stats</c> virtual table is accessible via PgWire
/// after running ANALYZE through the engine.
/// </summary>
public class WalhallaSqlPgWireStatsTests
{
    [Fact]
    public async Task PgStats_AfterAnalyze_ReturnsColumnRows()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();

        // Set up table and data directly via the engine
        scope.Engine.Execute("CREATE TABLE Employees (Id INT, Name VARCHAR(100))");
        scope.Engine.Execute("INSERT INTO Employees (Id, Name) VALUES (1, 'Alice')");
        scope.Engine.Execute("INSERT INTO Employees (Id, Name) VALUES (2, 'Bob')");
        scope.Engine.Execute("INSERT INTO Employees (Id, Name) VALUES (3, 'Alice')");
        scope.Engine.Execute("ANALYZE Employees");

        await using var conn = await scope.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT schemaname, tablename, attname, null_frac, avg_width, n_distinct FROM pg_stats WHERE tablename='Employees'",
            conn);

        var rowsByColumn = new Dictionary<string, (float NullFrac, int AvgWidth, float NDistinct)>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var col = reader.GetString(2);
            var nullFrac = reader.GetFloat(3);
            var avgWidth = reader.GetInt32(4);
            var nDistinct = reader.GetFloat(5);

            Assert.Equal("public", schema);
            Assert.Equal("Employees", table);
            rowsByColumn[col] = (nullFrac, avgWidth, nDistinct);
        }

        Assert.True(rowsByColumn.ContainsKey("Id") || rowsByColumn.ContainsKey("Name"),
            "Expected at least one column row from pg_stats for table 'Employees'.");
    }

    [Fact]
    public async Task PgStats_WithoutFilter_ReturnsMultipleTables()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();

        scope.Engine.Execute("CREATE TABLE TableA (X INT)");
        scope.Engine.Execute("INSERT INTO TableA (X) VALUES (1)");
        scope.Engine.Execute("ANALYZE TableA");

        scope.Engine.Execute("CREATE TABLE TableB (Y INT)");
        scope.Engine.Execute("INSERT INTO TableB (Y) VALUES (2)");
        scope.Engine.Execute("ANALYZE TableB");

        await using var conn = await scope.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT tablename FROM pg_stats", conn);

        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tableNames.Add(reader.GetString(0));

        Assert.Contains("TableA", tableNames);
        Assert.Contains("TableB", tableNames);
    }

    [Fact]
    public async Task PgStats_TableWithNoStats_ReturnsNoRows()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();

        scope.Engine.Execute("CREATE TABLE EmptyTable (X INT)");
        // No ANALYZE — stats should not exist

        await using var conn = await scope.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_stats WHERE tablename='EmptyTable'", conn);

        var count = await cmd.ExecuteScalarAsync();
        Assert.Equal(0L, count);
    }
}
