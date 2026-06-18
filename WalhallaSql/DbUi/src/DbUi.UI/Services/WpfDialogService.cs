using System.Windows;
using DbUi.UI.ViewModels;
using DbUi.UI.Views;
using DbUi.Core.Workspace;

namespace DbUi.UI.Services;

public class WpfDialogService : IDialogService
{
    public WorkspaceConnectionInfo? ShowOpenDatabaseDialog()
    {
        var vm = new OpenDatabaseViewModel();
        var dlg = new OpenDatabaseDialog(vm)
        {
            Owner = Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true ? vm.Result : null;
    }
}
