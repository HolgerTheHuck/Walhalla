using DbUi.Core.Catalog;
using DbUi.UI.Services;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public static class CatalogTreeNodeFactory
{
    public static TreeNodeViewModel Create(
        CatalogNode node,
        ICatalogBrowser catalog,
        Action<string> insertQuery,
        IDialogService dialogService)
    {
        return node.NodeKind switch
        {
            CatalogNodeKind.Server => new ServerNode(node, catalog, insertQuery, dialogService),
            CatalogNodeKind.Database => new DatabaseNode(node, catalog, insertQuery, dialogService),
            CatalogNodeKind.Folder => new FolderNode(node.DisplayName, async () =>
                await LoadChildrenAsync(node, catalog, insertQuery, dialogService), node, dialogService, insertQuery),
            CatalogNodeKind.Table => new TableNode(node, catalog, insertQuery, dialogService),
            CatalogNodeKind.View => new ViewNode(node, catalog, insertQuery, dialogService),
            CatalogNodeKind.Routine => new StoredProcedureNode(node, insertQuery),
            CatalogNodeKind.Column => new ColumnNode(node.DisplayName),
            CatalogNodeKind.PrimaryKey => new PrimaryKeyNode(node.DisplayName),
            CatalogNodeKind.ForeignKey => new ForeignKeyNode(node.DisplayName),
            CatalogNodeKind.Index => new IndexNode(node, insertQuery),
            CatalogNodeKind.Constraint => new ConstraintNode(node.DisplayName),
            _ => new FolderNode(node.DisplayName, async () =>
                await LoadChildrenAsync(node, catalog, insertQuery, dialogService), node, dialogService, insertQuery),
        };
    }

    private static async Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync(
        CatalogNode node,
        ICatalogBrowser catalog,
        Action<string> insertQuery,
        IDialogService dialogService)
    {
        var children = await catalog.GetChildrenAsync(node.Id);
        return children.Select(child => Create(child, catalog, insertQuery, dialogService));
    }
}
