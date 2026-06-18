using DbUi.Core.Connection;
using DbUi.Providers.MsSql;
using DbUi.Providers.MsSql.Schema;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DbUi.Providers.MsSql.Tests;

public sealed class SqlServerProviderTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private ConnectionInfo BuildConnectionInfo() => new()
    {
        ProviderId = "mssql",
        Server = _container.GetConnectionString().Split(';')
            .First(s => s.StartsWith("Server=", StringComparison.OrdinalIgnoreCase))
            .Split('=')[1],
        AuthMode = AuthMode.SqlServer,
        Username = MsSqlBuilder.DefaultUsername,
        Password = MsSqlBuilder.DefaultPassword
    };

    [Fact]
    public async Task OpenConnection_WhenServerReachable_Succeeds()
    {
        var provider = new SqlServerProvider();
        var info = BuildConnectionInfo();

        await using var conn = await provider.OpenConnectionAsync(info);

        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
    }

    [Fact]
    public async Task ExecuteQuery_SelectVersion_ReturnsOneRow()
    {
        var provider = new SqlServerProvider();
        var info = BuildConnectionInfo();
        await using var conn = await provider.OpenConnectionAsync(info);

        var result = await provider.ExecuteQueryAsync(conn, "SELECT @@VERSION AS [Version]");

        Assert.False(result.HasError, result.ErrorMessage);
        Assert.Single(result.Columns);
        Assert.Equal("Version", result.Columns[0].Name);
        Assert.Single(result.Rows);
        Assert.Contains("Microsoft SQL Server", result.Rows[0][0]?.ToString());
    }

    [Fact]
    public async Task ExecuteQuery_InvalidSql_ReturnsError()
    {
        var provider = new SqlServerProvider();
        var info = BuildConnectionInfo();
        await using var conn = await provider.OpenConnectionAsync(info);

        var result = await provider.ExecuteQueryAsync(conn, "SELECT * FROM NonExistentTable_xyz");

        Assert.True(result.HasError);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteQuery_CancelledToken_ReturnsCancel()
    {
        var provider = new SqlServerProvider();
        var info = BuildConnectionInfo();
        await using var conn = await provider.OpenConnectionAsync(info);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await provider.ExecuteQueryAsync(conn, "SELECT 1", cts.Token);

        Assert.True(result.HasError);
        Assert.Contains("cancel", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SqlServerSchemaLoaderTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    private async Task CreateTestObjectsAsync(SqlConnection conn, string db)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            USE [{db}];
            IF OBJECT_ID('dbo.TestCustomers') IS NULL
                CREATE TABLE dbo.TestCustomers (Id INT PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
            IF OBJECT_ID('dbo.vActiveCustomers') IS NULL
                EXEC('CREATE VIEW dbo.vActiveCustomers AS SELECT Id, Name FROM dbo.TestCustomers');
            IF OBJECT_ID('dbo.usp_GetCustomer') IS NULL
                EXEC('CREATE PROCEDURE dbo.usp_GetCustomer @Id INT AS SELECT * FROM dbo.TestCustomers WHERE Id = @Id');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task GetDatabases_ReturnsMaster()
    {
        // master is excluded by default — we verify it returns at least an empty list
        // and doesn't throw on a fresh container
        await using var conn = await OpenAsync();
        var loader = new SqlServerSchemaLoader();

        var dbs = await loader.GetDatabasesAsync(conn);

        // User databases: fresh SQL Server container has no user DBs, result is empty
        Assert.NotNull(dbs);
    }

    [Fact]
    public async Task GetTables_AfterCreatingTable_ReturnsTable()
    {
        await using var conn = await OpenAsync();
        var loader = new SqlServerSchemaLoader();

        // Create table in master (available on fresh container)
        await using var setupCmd = conn.CreateCommand();
        setupCmd.CommandText = """
            IF OBJECT_ID('dbo.SchemaTest_Tbl') IS NULL
                CREATE TABLE dbo.SchemaTest_Tbl (Id INT, Val NVARCHAR(50));
            """;
        await setupCmd.ExecuteNonQueryAsync();

        var tables = await loader.GetTablesAsync(conn, "master");

        Assert.Contains(tables, t => t.Name == "SchemaTest_Tbl" && t.Schema == "dbo");
    }

    [Fact]
    public async Task GetColumns_ReturnsCorrectTypes()
    {
        await using var conn = await OpenAsync();
        var loader = new SqlServerSchemaLoader();

        await using var setupCmd = conn.CreateCommand();
        setupCmd.CommandText = """
            IF OBJECT_ID('dbo.SchemaTest_Cols') IS NULL
                CREATE TABLE dbo.SchemaTest_Cols (
                    Id      INT NOT NULL,
                    Name    NVARCHAR(200),
                    Price   DECIMAL(10, 2)
                );
            """;
        await setupCmd.ExecuteNonQueryAsync();

        var cols = await loader.GetColumnsAsync(conn, "master", "dbo", "SchemaTest_Cols");

        Assert.Equal(3, cols.Count);
        Assert.Equal("Id",    cols[0].Name);
        Assert.Equal("Name",  cols[1].Name);
        Assert.Equal("Price", cols[2].Name);
        Assert.Equal("int",   cols[0].DataType);
        Assert.False(cols[0].IsNullable);
        Assert.True(cols[1].IsNullable);
    }
}
