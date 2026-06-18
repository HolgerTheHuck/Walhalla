using DbUi.Core.Catalog;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed class DatabaseNode : TreeNodeViewModel
{
    private readonly CatalogNode _node;
    private readonly ICatalogBrowser _catalog;
    private readonly Action<string> _insertQuery;

    public DatabaseNode(CatalogNode node, ICatalogBrowser catalog, Action<string> insertQuery)
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
}
