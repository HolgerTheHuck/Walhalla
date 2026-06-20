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

public record SchemaPrimaryKey(string Name, IReadOnlyList<string> ColumnNames)
{
    public string DisplayText => ColumnNames.Count == 1
        ? $"{Name} ({ColumnNames[0]})"
        : $"{Name} ({string.Join(", ", ColumnNames)})";
}

public record SchemaForeignKey(
    string Name,
    IReadOnlyList<string> ColumnNames,
    string ReferencedCollection,
    IReadOnlyList<string> ReferencedColumns,
    string OnDelete,
    string OnUpdate)
{
    public string DisplayText
    {
        get
        {
            var local = string.Join(", ", ColumnNames);
            var remote = string.Join(", ", ReferencedColumns);
            return $"{Name}: ({local}) → {ReferencedCollection}({remote})";
        }
    }
}

public record SchemaIndex(
    string Name,
    IReadOnlyList<string> ColumnNames,
    bool IsUnique,
    string IndexType,
    string? TargetProjectionName = null,
    bool IsInternal = false)
{
    public string DisplayText
    {
        get
        {
            var columns = string.Join(", ", ColumnNames);
            var suffix = string.IsNullOrWhiteSpace(TargetProjectionName)
                ? ""
                : $" (projection {TargetProjectionName})";
            var unique = IsUnique ? "UNIQUE " : "";
            return $"{unique}{Name} ({columns}){suffix}";
        }
    }
}
