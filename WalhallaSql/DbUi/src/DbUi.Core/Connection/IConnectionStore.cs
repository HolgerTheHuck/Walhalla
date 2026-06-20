using DbUi.Core.Workspace;

namespace DbUi.Core.Connection;

public interface IConnectionStore
{
    Task<IReadOnlyList<WorkspaceConnectionInfo>> LoadAsync();
    Task SaveAsync(IEnumerable<WorkspaceConnectionInfo> connections);

    /// <summary>
    /// Lädt den zuletzt verwendeten Verbindungsdatensatz.
    /// </summary>
    Task<WorkspaceConnectionInfo?> LoadLastAsync();

    /// <summary>
    /// Speichert den Verbindungsdatensatz als ersten Eintrag der Recent-Liste.
    /// </summary>
    Task SaveRecentAsync(WorkspaceConnectionInfo connection, int maxEntries = 10);
}
