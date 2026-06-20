namespace DbUi.Core.Catalog;

public sealed record CatalogTable(string Name, IReadOnlyList<string> Columns);

public sealed record CatalogProcedure(string Name, IReadOnlyList<string> Parameters);

public sealed record CatalogSnapshot(
    IReadOnlyList<CatalogTable> Tables,
    IReadOnlyList<CatalogProcedure> Procedures);
