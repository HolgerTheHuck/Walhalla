using DbUi.Core.Catalog;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public static class CatalogTreeNodeFactory
{
    public static TreeNodeViewModel Create(
        CatalogNode node,
        ICatalogBrowser catalog,
        Action<string> insertQuery)
    {
        return node.NodeKind switch
        {
            CatalogNodeKind.Server => new ServerNode(node, catalog, insertQuery),
            CatalogNodeKind.Database => new DatabaseNode(node, catalog, insertQuery),
            CatalogNodeKind.Folder => new FolderNode(node.DisplayName, async () =>
                await LoadChildrenAsync(node, catalog, insertQuery)),
            CatalogNodeKind.Table => new TableNode(node, catalog, insertQuery),
            CatalogNodeKind.View => new ViewNode(node, catalog, insertQuery),
            CatalogNodeKind.Routine => new ProcedureNode(node.DisplayName),
            CatalogNodeKind.Column => new ColumnNode(node.DisplayName),
            _ => new FolderNode(node.DisplayName, async () =>
                await LoadChildrenAsync(node, catalog, insertQuery)),
        };
    }

    private static async Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync(
        CatalogNode node,
        ICatalogBrowser catalog,
        Action<string> insertQuery)
    {
        var children = await catalog.GetChildrenAsync(node.Id);
        return children.Select(child => Create(child, catalog, insertQuery));
    }
}
