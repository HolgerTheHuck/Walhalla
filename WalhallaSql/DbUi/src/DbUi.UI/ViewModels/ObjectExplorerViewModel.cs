using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Catalog;
using DbUi.Core.Workspace;
using DbUi.UI.Services;
using DbUi.UI.ViewModels.ObjectExplorer;
using System.Collections.ObjectModel;

namespace DbUi.UI.ViewModels;

public partial class ObjectExplorerViewModel : ObservableObject
{
    private IWorkspaceSession? _session;
    private readonly IDialogService _dialogService;

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];

    public Action<string>? InsertQuery { get; set; }

    public ObjectExplorerViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public async Task OnConnectedAsync(IWorkspaceSession session)
    {
        _session = session;
        await LoadRootNodesAsync();
    }

    public void OnDisconnected()
    {
        _session = null;
        RootNodes.Clear();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_session is null)
            return;

        await LoadRootNodesAsync();
    }

    private async Task LoadRootNodesAsync()
    {
        if (_session is null)
            return;

        var rootNodes = await _session.Catalog.GetRootNodesAsync();
        RootNodes.Clear();
        foreach (var rootNode in rootNodes)
        {
            var node = CatalogTreeNodeFactory.Create(rootNode, _session.Catalog, sql => InsertQuery?.Invoke(sql), _dialogService);
            RootNodes.Add(node);
            node.IsExpanded = true;
        }
    }
}
