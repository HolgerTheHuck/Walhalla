using System.Threading;
using DbUi.Core.Workspace;
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

        // Engine-Öffnung und Connection-Setup synchron auf einem Hintergrund-Thread,
        // damit das Öffnen großer/corrupt Datenbanken den WPF-UI-Thread nicht einfriert.
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

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
        }, cancellationToken);
    }
}
