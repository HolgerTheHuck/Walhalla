using System;
using System.Collections.Generic;

namespace WalhallaSql.Sql;

public enum SqlIndexType
{
    BTree = 0,
    Gin = 1
}

public enum SqlProjectionMaterializationMode
{
    Virtual,
    Persisted,
    IndexedOnly
}

public enum SqlStringIndexNormalizationKind
{
    Unspecified,
    None,
    OrdinalIgnoreCaseUpperInvariant
}

public sealed record SqlProjectionDefinition
{
    public SqlProjectionDefinition()
    {
    }

    public SqlProjectionDefinition(
        string projectionName,
        string sourceColumnName,
        IReadOnlyList<string> pathSegments,
        SqlScalarType resultType,
        SqlProjectionMaterializationMode materializationMode = SqlProjectionMaterializationMode.IndexedOnly)
    {
        if (string.IsNullOrWhiteSpace(projectionName))
            throw new ArgumentException("Projection name must be provided.", nameof(projectionName));
        if (string.IsNullOrWhiteSpace(sourceColumnName))
            throw new ArgumentException("Source column name must be provided.", nameof(sourceColumnName));

        ProjectionName = projectionName;
        SourceColumnName = sourceColumnName;
        PathSegments = pathSegments ?? Array.Empty<string>();
        ResultType = resultType;
        MaterializationMode = materializationMode;
    }

    public string ProjectionName { get; init; } = string.Empty;
    public string SourceColumnName { get; init; } = string.Empty;
    public IReadOnlyList<string> PathSegments { get; init; } = Array.Empty<string>();
    public SqlScalarType ResultType { get; init; } = SqlScalarType.Unknown;
    public SqlProjectionMaterializationMode MaterializationMode { get; init; } = SqlProjectionMaterializationMode.IndexedOnly;
}

public sealed record SqlIndexDefinition
{
    public SqlIndexDefinition()
    {
    }

    public SqlIndexDefinition(string indexName, IReadOnlyList<string> columnNames, bool isUnique)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("Index name must be provided.", nameof(indexName));
        if (columnNames == null || columnNames.Count == 0)
            throw new ArgumentException("At least one index column must be provided.", nameof(columnNames));

        IndexName = indexName;
        ColumnNames = columnNames;
        IsUnique = isUnique;
    }

    public SqlIndexDefinition(string indexName, string columnName, bool isUnique)
        : this(indexName, new[] { columnName }, isUnique)
    {
    }

    public string IndexName { get; init; } = string.Empty;
    public IReadOnlyList<string> ColumnNames { get; init; } = Array.Empty<string>();
    public string? TargetProjectionName { get; init; }
    public bool IsUnique { get; init; }
    public bool IsInternal { get; init; }
    public SqlIndexType IndexType { get; init; } = SqlIndexType.BTree;
    public int StorageFormatVersion { get; init; }
    public SqlStringIndexNormalizationKind StringNormalization { get; init; } = SqlStringIndexNormalizationKind.Unspecified;

    public bool TargetsProjection => !string.IsNullOrWhiteSpace(TargetProjectionName);
    public string ColumnName => ColumnNames.Count > 0 ? ColumnNames[0] : string.Empty;

    public static SqlIndexDefinition ForProjection(string indexName, string projectionName, bool isUnique)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("Index name must be provided.", nameof(indexName));
        if (string.IsNullOrWhiteSpace(projectionName))
            throw new ArgumentException("Projection name must be provided.", nameof(projectionName));

        return new SqlIndexDefinition
        {
            IndexName = indexName,
            TargetProjectionName = projectionName,
            ColumnNames = Array.Empty<string>(),
            IsUnique = isUnique
        };
    }
}
