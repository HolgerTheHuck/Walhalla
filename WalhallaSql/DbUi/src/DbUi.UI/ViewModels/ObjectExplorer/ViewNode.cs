using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Catalog;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed partial class ViewNode : TreeNodeViewModel
{
    private readonly CatalogNode _node;
    private readonly ICatalogBrowser _catalog;
    private readonly Action<string> _insertQuery;

    public ViewNode(CatalogNode node, ICatalogBrowser catalog, Action<string> insertQuery)
        : base(node.DisplayName, hasChildren: node.HasChildren)
    {
        _node = node;
        _catalog = catalog;
        _insertQuery = insertQuery;
    }

    protected override async Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync()
    {
        var children = await _catalog.GetChildrenAsync(_node.Id);
        return children.Select(child => CatalogTreeNodeFactory.Create(child, _catalog, _insertQuery));
    }

    private string? GetActionCommandText(string actionId) =>
        _node.Actions?.FirstOrDefault(action => string.Equals(action.ActionId, actionId, StringComparison.OrdinalIgnoreCase))?.CommandText;

    [RelayCommand]
    private void ScriptSelectTop()
    {
        var sql = GetActionCommandText("select-top");
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }
}
