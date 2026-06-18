using DbUi.Core.Workspace;

namespace DbUi.UI.Services;

public interface IDialogService
{
    WorkspaceConnectionInfo? ShowOpenDatabaseDialog();
}
