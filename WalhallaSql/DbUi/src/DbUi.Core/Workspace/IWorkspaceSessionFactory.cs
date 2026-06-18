namespace DbUi.Core.Workspace;

public interface IWorkspaceSessionFactory
{
    Task<IWorkspaceSession> CreateAsync(WorkspaceConnectionInfo connectionInfo);
}
