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
    public async Task TrustAuth_NoUsersConfigured_ConnectsWithoutPassword()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        await using var conn = await scope.OpenConnectionAsync();

        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }
}
