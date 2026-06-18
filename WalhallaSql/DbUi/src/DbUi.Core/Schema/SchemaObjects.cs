namespace DbUi.Core.Schema;

public record SchemaTable(string Schema, string Name)
{
    public string FullName => $"[{Schema}].[{Name}]";
}

public record SchemaView(string Schema, string Name)
{
    public string FullName => $"[{Schema}].[{Name}]";
}

public record SchemaProcedure(string Schema, string Name)
{
    public string FullName => $"[{Schema}].[{Name}]";
}

public record SchemaColumn(
    string Name,
    string DataType,
    bool IsNullable,
    int? MaxLength,
    int? Precision,
    int? Scale)
{
    public string TypeSummary => DataType.ToUpperInvariant() switch
    {
        "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR"
            when MaxLength is not null => $"{DataType}({(MaxLength == -1 ? "MAX" : MaxLength)})",
        "DECIMAL" or "NUMERIC"
            when Precision is not null => $"{DataType}({Precision},{Scale ?? 0})",
        _ => DataType
    };

    public string DisplayText => $"{Name}  ({TypeSummary}{(IsNullable ? ", null" : "")})";
}
