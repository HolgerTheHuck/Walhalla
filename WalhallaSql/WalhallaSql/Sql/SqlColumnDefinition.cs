namespace WalhallaSql.Sql;

public sealed record SqlColumnDefinition(
    string Name,
    SqlScalarType Type,
    bool IsNullable = true,
    bool IsPrimaryKey = false,
    bool IsUnique = false,
    string? Collation = null);
