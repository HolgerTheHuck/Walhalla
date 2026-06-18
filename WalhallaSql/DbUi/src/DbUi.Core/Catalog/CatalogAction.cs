namespace DbUi.Core.Catalog;

public sealed record CatalogAction(
    string ActionId,
    string DisplayName,
    string? CommandText = null);
