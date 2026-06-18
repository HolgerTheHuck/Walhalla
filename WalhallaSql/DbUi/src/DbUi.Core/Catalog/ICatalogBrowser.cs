namespace DbUi.Core.Catalog;

public interface ICatalogBrowser
{
    Task<IReadOnlyList<CatalogNode>> GetRootNodesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogNode>> GetChildrenAsync(
        CatalogNodeId nodeId,
        CancellationToken cancellationToken = default);
}
