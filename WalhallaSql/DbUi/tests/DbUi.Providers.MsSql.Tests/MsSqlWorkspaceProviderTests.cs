using DbUi.Core.Catalog;
using DbUi.Core.Workspace;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DbUi.Providers.MsSql.Tests;

public sealed class MsSqlWorkspaceProviderTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task OpenSession_WhenServerReachable_ExposesCapabilitiesAndQueryRunner()
    {
        var workspaceProvider = new MsSqlWorkspaceProvider(new SqlServerProvider());

        await using var session = await workspaceProvider.OpenSessionAsync(BuildWorkspaceConnectionInfo());

        Assert.True(session.Capabilities.CanExecuteTextQueries);
        Assert.True(session.Capabilities.CanBrowseCatalog);

        var result = await session.Queries.ExecuteAsync(new("SELECT @@VERSION AS [Version]"));

        Assert.False(result.HasError, result.ErrorMessage);
        Assert.Single(result.Rows);
        Assert.Contains("Microsoft SQL Server", result.Rows[0][0]?.ToString());
    }

    [Fact]
    public async Task CatalogBrowser_WhenUserDatabaseExists_ReturnsDatabaseFoldersAndTables()
    {
        var databaseName = "DbUiWorkspaceTest";
        await EnsureUserDatabaseAsync(databaseName);

        var workspaceProvider = new MsSqlWorkspaceProvider(new SqlServerProvider());
        await using var session = await workspaceProvider.OpenSessionAsync(BuildWorkspaceConnectionInfo(databaseName));

        var roots = await session.Catalog.GetRootNodesAsync();
        var serverNode = Assert.Single(roots);

        var databaseNodes = await session.Catalog.GetChildrenAsync(serverNode.Id);
        var databaseNode = Assert.Single(databaseNodes, node =>
            node.NodeKind == CatalogNodeKind.Database
            && string.Equals(node.DisplayName, databaseName, StringComparison.OrdinalIgnoreCase));

        var folders = await session.Catalog.GetChildrenAsync(databaseNode.Id);
        var tablesFolder = Assert.Single(folders, node =>
            node.NodeKind == CatalogNodeKind.Folder
            && string.Equals(node.DisplayName, "Tables", StringComparison.OrdinalIgnoreCase));

        var tableNodes = await session.Catalog.GetChildrenAsync(tablesFolder.Id);
        Assert.Contains(tableNodes, node =>
            node.NodeKind == CatalogNodeKind.Table
            && string.Equals(node.DisplayName, "dbo.WorkspaceItems", StringComparison.OrdinalIgnoreCase));
    }

    private WorkspaceConnectionInfo BuildWorkspaceConnectionInfo(string? database = null)
    {
        var server = _container.GetConnectionString().Split(';')
            .First(segment => segment.StartsWith("Server=", StringComparison.OrdinalIgnoreCase))
            .Split('=')[1];

        var connectionInfo = new WorkspaceConnectionInfo
        {
            ProviderId = "mssql",
            DisplayName = string.IsNullOrWhiteSpace(database) ? server : $"{server}\\{database}",
            ConnectionKind = "sqlserver",
        };
        connectionInfo.SetSetting("server", server);
        connectionInfo.SetSetting("authMode", nameof(Core.Connection.AuthMode.SqlServer));
        connectionInfo.SetSetting("username", MsSqlBuilder.DefaultUsername);
        connectionInfo.SetSetting("password", MsSqlBuilder.DefaultPassword);
        connectionInfo.SetSetting("database", database);
        return connectionInfo;
    }

    private async Task EnsureUserDatabaseAsync(string databaseName)
    {
        await using var conn = new SqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();

        await using (var createDb = conn.CreateCommand())
        {
            createDb.CommandText = $"IF DB_ID('{databaseName}') IS NULL CREATE DATABASE [{databaseName}]";
            await createDb.ExecuteNonQueryAsync();
        }

        await using var createTable = conn.CreateCommand();
        createTable.CommandText = $"""
            USE [{databaseName}];
            IF OBJECT_ID('dbo.WorkspaceItems') IS NULL
                CREATE TABLE dbo.WorkspaceItems (Id INT PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
            """;
        await createTable.ExecuteNonQueryAsync();
    }
}
