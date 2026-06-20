using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Catalog;
using DbUi.UI.Services;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed partial class DatabaseNode : TreeNodeViewModel
{
    private readonly CatalogNode _node;
    private readonly ICatalogBrowser _catalog;
    private readonly Action<string> _insertQuery;
    private readonly IDialogService _dialogService;

    public DatabaseNode(CatalogNode node, ICatalogBrowser catalog, Action<string> insertQuery, IDialogService dialogService)
        : base(node.DisplayName, hasChildren: node.HasChildren)
    {
        _node = node;
        _catalog = catalog;
        _insertQuery = insertQuery;
        _dialogService = dialogService;
    }

    protected override async Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync()
    {
        var children = await _catalog.GetChildrenAsync(_node.Id);
        return children.Select(child => CatalogTreeNodeFactory.Create(child, _catalog, _insertQuery, _dialogService));
    }

    [RelayCommand]
    private void NewTable()
    {
        var sql = _dialogService.ShowCreateTableDialog();
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }
}
