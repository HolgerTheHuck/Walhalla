using Npgsql;
using Xunit;

namespace WalhallaSql.PgWire.Tests;

/// <summary>
/// Tests verifying SCRAM-SHA-256 authentication through the PgWire frontend.
/// </summary>
public class WalhallaSqlPgWireAuthTests
{
    [Fact]
    public async Task ScramAuth_ValidPassword_ConnectsSuccessfully()
    {
        await using var scope = await WalhallaSqlPgWireAuthTestScope.CreateAsync("testuser", "testpass");
        await using var conn = await scope.OpenConnectionAsync();

        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task ScramAuth_InvalidPassword_ThrowsAuthenticationException()
    {
        await using var scope = await WalhallaSqlPgWireAuthTestScope.CreateAsync("testuser", "testpass");

        var ex = await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
        {
            await using var conn = scope.OpenConnectionWithPassword("wrongpass");
            await conn.OpenAsync();
        });

        Assert.Contains("28P01", ex.Message);
    }


    [Fact]
    public async Task ScramAuth_NonLoginRole_ThrowsAuthenticationException()
    {
        await using var scope = await WalhallaSqlPgWireAuthTestScope.CreateAsync("service", "servicepass", canLogin: false);

        var ex = await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
        {
            await using var conn = await scope.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync();
        });

        Assert.Contains("28P01", ex.Message);
    }

    [Fact]
    public async Task ScramAuth_CurrentRole_EnforcesTablePrivileges()
    {
        await using var scope = await WalhallaSqlPgWireAuthTestScope.CreateAsync("reader", "readerpass", canLogin: true);

        // Tabelle als Superuser postgres anlegen und Leserecht auf reader gewaehren
        scope.Engine.CurrentRole = "postgres";
        scope.Engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        scope.Engine.Execute("GRANT SELECT ON TABLE T TO reader");

        // Als reader ueber PgWire verbinden
        await using var conn = scope.OpenConnectionAs("reader", "readerpass");
        await conn.OpenAsync();

        await using (var cmd = new NpgsqlCommand("SELECT * FROM T", conn))
        {
            var result = await cmd.ExecuteScalarAsync();
            Assert.Null(result);
        }

        // Schreibzugriff muss abgelehnt werden
        await using (var cmd = new NpgsqlCommand("INSERT INTO T (Id) VALUES (1)", conn))
        {
            var ex = await Assert.ThrowsAnyAsync<NpgsqlException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains("42501", ex.Message);
        }
    }
}
