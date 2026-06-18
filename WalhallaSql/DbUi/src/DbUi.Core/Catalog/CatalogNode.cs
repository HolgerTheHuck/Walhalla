namespace DbUi.Core.Catalog;

public sealed record CatalogNode(
    CatalogNodeId Id,
    string DisplayName,
    CatalogNodeKind NodeKind,
    bool HasChildren,
    IReadOnlyList<CatalogAction>? Actions = null,
    IReadOnlyDictionary<string, string?>? Metadata = null);
