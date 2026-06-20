using System.Windows;
using System.Windows.Forms;
using DbUi.UI.ViewModels;
using DbUi.UI.ViewModels.Dialogs;
using DbUi.UI.Views;
using DbUi.Core.Connection;
using DbUi.Core.Workspace;

namespace DbUi.UI.Services;

public class WpfDialogService : IDialogService
{
    public WorkspaceConnectionInfo? ShowOpenDatabaseDialog(IConnectionStore connectionStore)
    {
        var vm = new OpenDatabaseViewModel();
        vm.BrowseFolderCallback = ShowFolderBrowserDialog;
        _ = LoadRecentAsync(vm, connectionStore);

        var dlg = new OpenDatabaseDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        var result = dlg.ShowDialog() == true ? vm.Result : null;

        if (result is not null)
            _ = SaveRecentAsync(result, connectionStore);

        return result;
    }

    private static async Task LoadRecentAsync(OpenDatabaseViewModel vm, IConnectionStore connectionStore)
    {
        try
        {
            var recent = await connectionStore.LoadAsync();
            foreach (var connection in recent)
                vm.RecentConnections.Add(connection);

            var last = await connectionStore.LoadLastAsync();
            if (last is not null)
                vm.SelectedRecentConnection = last;
        }
        catch
        {
            // Best-effort: leere Recent-Liste ist akzeptabel.
        }
    }

    private static async Task SaveRecentAsync(WorkspaceConnectionInfo result, IConnectionStore connectionStore)
    {
        try
        {
            await connectionStore.SaveRecentAsync(result);
        }
        catch
        {
            // Best-effort.
        }
    }

    public WorkspaceConnectionInfo? ShowNewDatabaseDialog()
    {
        var vm = new NewDatabaseViewModel();
        vm.BrowseFolderCallback = ShowFolderBrowserDialog;

        var dlg = new NewDatabaseDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true ? vm.Result : null;
    }

    public string? ShowFolderBrowserDialog(string description)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = description,
            ShowNewFolderButton = true,
        };

        var result = dlg.ShowDialog();
        return result == DialogResult.OK ? dlg.SelectedPath : null;
    }

    public string? ShowCreateTableDialog()
    {
        var vm = new CreateTableViewModel();
        var dlg = new CreateTableDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true ? vm.Result : null;
    }

    public string? ShowCreateIndexDialog(string tableName, IReadOnlyList<string> availableColumns)
    {
        var vm = new CreateIndexViewModel(tableName, availableColumns);
        var dlg = new CreateIndexDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true ? vm.Result : null;
    }

    public string? ShowCreateProcedureDialog()
    {
        var vm = new CreateProcedureViewModel();
        var dlg = new CreateProcedureDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true ? vm.Result : null;
    }

    public string? ShowCreateTriggerDialog(string tableName)
    {
        var vm = new CreateTriggerViewModel(tableName);
        var dlg = new CreateTriggerDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true ? vm.Result : null;
    }
}
