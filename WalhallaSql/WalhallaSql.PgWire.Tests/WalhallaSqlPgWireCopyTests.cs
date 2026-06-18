using Xunit;
using Npgsql;

namespace WalhallaSql.PgWire.Tests;

public class WalhallaSqlPgWireCopyTests
{
    private static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CopyFromStdin_Text_InsertsRows()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE copy_text (id INT, name TEXT)");

        await using (var writer = await conn.BeginTextImportAsync("COPY copy_text (id, name) FROM STDIN"))
        {
            await writer.WriteLineAsync("1\tAlice");
            await writer.WriteLineAsync("2\tBob");
        }

        await using var cmd = new NpgsqlCommand("SELECT * FROM copy_text ORDER BY id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Alice", reader.GetString(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("Bob", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task CopyFromStdin_Csv_InsertsRows()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE copy_csv (id INT, name TEXT)");

        await using (var writer = await conn.BeginTextImportAsync("COPY copy_csv (id, name) FROM STDIN WITH (FORMAT CSV)"))
        {
            await writer.WriteLineAsync("1,Alice");
            await writer.WriteLineAsync("2,Bob");
        }

        await using var cmd = new NpgsqlCommand("SELECT * FROM copy_csv ORDER BY id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Alice", reader.GetString(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("Bob", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task CopyFromStdin_CsvWithHeader_InsertsRows()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE copy_hdr (id INT, name TEXT)");

        await using (var writer = await conn.BeginTextImportAsync("COPY copy_hdr (id, name) FROM STDIN WITH (FORMAT CSV, HEADER)"))
        {
            await writer.WriteLineAsync("id,name"); // header
            await writer.WriteLineAsync("1,Alice");
            await writer.WriteLineAsync("2,Bob");
        }

        await using var cmd = new NpgsqlCommand("SELECT * FROM copy_hdr ORDER BY id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Alice", reader.GetString(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("Bob", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task CopyToStdout_Text_ReturnsRows()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE copy_out (id INT, name TEXT)");
        await Execute(conn, "INSERT INTO copy_out (id, name) VALUES (1, 'Alice')");
        await Execute(conn, "INSERT INTO copy_out (id, name) VALUES (2, 'Bob')");

        using var reader = await conn.BeginTextExportAsync("COPY copy_out TO STDOUT");
        var line1 = await reader.ReadLineAsync();
        var line2 = await reader.ReadLineAsync();
        Assert.Null(await reader.ReadLineAsync());

        Assert.Equal("1\tAlice", line1);
        Assert.Equal("2\tBob", line2);
    }

    [Fact]
    public async Task CopyToStdout_Csv_ReturnsRows()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE copy_out_csv (id INT, name TEXT)");
        await Execute(conn, "INSERT INTO copy_out_csv (id, name) VALUES (1, 'Alice')");
        await Execute(conn, "INSERT INTO copy_out_csv (id, name) VALUES (2, 'Bob')");

        using var reader = await conn.BeginTextExportAsync("COPY copy_out_csv TO STDOUT WITH (FORMAT CSV)");
        var line1 = await reader.ReadLineAsync();
        var line2 = await reader.ReadLineAsync();
        Assert.Null(await reader.ReadLineAsync());

        Assert.Equal("1,Alice", line1);
        Assert.Equal("2,Bob", line2);
    }

    [Fact]
    public async Task CopyToStdout_CsvWithHeader_ReturnsRowsWithHeader()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE copy_out_hdr (id INT, name TEXT)");
        await Execute(conn, "INSERT INTO copy_out_hdr (id, name) VALUES (1, 'Alice')");

        using var reader = await conn.BeginTextExportAsync("COPY copy_out_hdr TO STDOUT WITH (FORMAT CSV, HEADER)");
        var line1 = await reader.ReadLineAsync(); // header
        var line2 = await reader.ReadLineAsync(); // data
        Assert.Null(await reader.ReadLineAsync());

        Assert.Equal("id,name", line1);
        Assert.Equal("1,Alice", line2);
    }

    [Fact]
    public async Task CopyToStdout_Nulls_RenderAsNullMarker()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await Execute(conn, "CREATE TABLE copy_null (id INT, name TEXT)");
        await Execute(conn, "INSERT INTO copy_null (id, name) VALUES (1, NULL)");

        using var reader = await conn.BeginTextExportAsync("COPY copy_null TO STDOUT");
        var line1 = await reader.ReadLineAsync();
        Assert.Equal("1\t\\N", line1); // TEXT null marker is \N
    }
}
