namespace DbUi.Core.Catalog;

public readonly record struct CatalogNodeId(string Value)
{
    public override string ToString() => Value;
}
