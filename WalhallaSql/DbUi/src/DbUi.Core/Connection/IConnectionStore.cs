using DbUi.Core.Workspace;

namespace DbUi.Core.Connection;

public interface IConnectionStore
{
    Task<IReadOnlyList<WorkspaceConnectionInfo>> LoadAsync();
    Task SaveAsync(IEnumerable<WorkspaceConnectionInfo> connections);
}
