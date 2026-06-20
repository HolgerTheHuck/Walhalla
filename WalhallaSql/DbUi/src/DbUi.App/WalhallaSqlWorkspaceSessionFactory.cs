using System.Data.Common;
using System.Threading;
using DbUi.Core.Workspace;
using Npgsql;
using WalhallaSql;
using WalhallaSql.AdoNet;

namespace DbUi.App;

public sealed class WalhallaSqlWorkspaceSessionFactory : IWorkspaceSessionFactory
{
    public Task<IWorkspaceSession> CreateAsync(WorkspaceConnectionInfo connectionInfo)
        => CreateAsync(connectionInfo, CancellationToken.None);

    public async Task<IWorkspaceSession> CreateAsync(WorkspaceConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (connectionInfo.Mode == WorkspaceConnectionMode.PgWire)
                return CreatePgWireSession(connectionInfo);

            return CreateLocalSession(connectionInfo);
        }, cancellationToken);
    }

    private static IWorkspaceSession CreateLocalSession(WorkspaceConnectionInfo connectionInfo)
    {
        WalhallaEngine engine;
        if (connectionInfo.IsInMemory)
        {
            engine = WalhallaEngine.InMemory();
        }
        else
        {
            engine = WalhallaEngine.Open(connectionInfo.StoragePath);
        }

        var databaseName = string.IsNullOrWhiteSpace(connectionInfo.DatabaseName)
            ? WalhallaSqlDbConnection.DefaultDatabaseName
            : connectionInfo.DatabaseName;

        return new WalhallaSqlWorkspaceSession(
            engine,
            connectionInfo.StoragePath,
            databaseName,
            connectionInfo.DisplayName);
    }

    private static IWorkspaceSession CreatePgWireSession(WorkspaceConnectionInfo connectionInfo)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = connectionInfo.PgWireHost,
            Port = connectionInfo.PgWirePort,
            Username = connectionInfo.PgWireUser,
            Password = connectionInfo.PgWirePassword,
            Database = connectionInfo.PgWireDatabase,
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();

        return new PgWireWorkspaceSession(connection, connectionInfo.DisplayName);
    }
}

