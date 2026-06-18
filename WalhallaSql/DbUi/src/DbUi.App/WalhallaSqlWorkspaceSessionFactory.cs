using DbUi.Core.Workspace;
using WalhallaSql;
using WalhallaSql.AdoNet;

namespace DbUi.App;

public sealed class WalhallaSqlWorkspaceSessionFactory : IWorkspaceSessionFactory
{
    public Task<IWorkspaceSession> CreateAsync(WorkspaceConnectionInfo connectionInfo)
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

        var session = new WalhallaSqlWorkspaceSession(
            engine,
            connectionInfo.StoragePath,
            databaseName,
            connectionInfo.DisplayName);

        return Task.FromResult<IWorkspaceSession>(session);
    }
}
