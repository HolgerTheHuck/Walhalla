using System;
using System.Collections.Generic;
using System.Linq;

namespace WalhallaSql.Sql;

public enum SqlForeignKeyAction
{
    Restrict,
    Cascade,
    SetNull
}

public sealed record SqlForeignKeyDefinition
{
    public SqlForeignKeyDefinition()
    {
    }

    public SqlForeignKeyDefinition(
        string constraintName,
        IReadOnlyList<string> columnNames,
        string referencedCollection,
        IReadOnlyList<string> referencedColumns,
        SqlForeignKeyAction onDelete = SqlForeignKeyAction.Restrict,
        SqlForeignKeyAction onUpdate = SqlForeignKeyAction.Restrict)
    {
        ConstraintName = constraintName;
        ColumnNames = columnNames;
        ReferencedCollection = referencedCollection;
        ReferencedColumns = referencedColumns;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }

    public SqlForeignKeyDefinition(
        string constraintName,
        string columnName,
        string referencedCollection,
        string referencedColumn,
        SqlForeignKeyAction onDelete = SqlForeignKeyAction.Restrict,
        SqlForeignKeyAction onUpdate = SqlForeignKeyAction.Restrict)
    {
        ConstraintName = constraintName;
        ColumnNames = new[] { columnName };
        ReferencedCollection = referencedCollection;
        ReferencedColumns = new[] { referencedColumn };
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }

    public string ConstraintName { get; init; } = string.Empty;
    public IReadOnlyList<string> ColumnNames { get; init; } = Array.Empty<string>();
    public string ReferencedCollection { get; init; } = string.Empty;
    public IReadOnlyList<string> ReferencedColumns { get; init; } = Array.Empty<string>();
    public SqlForeignKeyAction OnDelete { get; init; } = SqlForeignKeyAction.Restrict;
    public SqlForeignKeyAction OnUpdate { get; init; } = SqlForeignKeyAction.Restrict;
}

public sealed record SqlTableDefinition(
    string CollectionName,
    IReadOnlyList<SqlColumnDefinition> Columns,
    IReadOnlyList<SqlIndexDefinition> Indexes,
    IReadOnlyList<SqlForeignKeyDefinition>? ForeignKeys = null,
    IReadOnlyList<SqlProjectionDefinition>? Projections = null,
    IReadOnlyList<SqlCheckConstraint>? CheckConstraints = null)
{
    private IReadOnlyList<SqlColumnDefinition>? _primaryKeyColumns;

    public IReadOnlyList<SqlColumnDefinition> PrimaryKeyColumns =>
        _primaryKeyColumns ??= ComputePrimaryKeyColumns();

    private IReadOnlyList<SqlColumnDefinition> ComputePrimaryKeyColumns()
    {
        var primary = Columns.Where(c => c.IsPrimaryKey).ToArray();
        if (primary.Length > 0)
            return primary;

        var idColumn = Columns.FirstOrDefault(c =>
            c.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
        if (idColumn != null)
            return new[] { idColumn };

        throw new NotSupportedException(
            $"Collection '{CollectionName}' requires a primary key column.");
    }

    /// <summary>
    /// True if this table's primary key is a single-column integer
    /// (<see cref="SqlScalarType.Int32"/> or <see cref="SqlScalarType.Int64"/>) column.
    /// Such tables follow SQLite-style "INTEGER PRIMARY KEY" semantics: the user-supplied
    /// PK value IS the storage row id (no separate auto-rowid is allocated). Int32 values
    /// are widened losslessly to the Int64 row id space.
    /// </summary>
    /// <param name="columnIndex">Index of the PK column in <see cref="Columns"/>; -1 if not an alias PK.</param>
    public bool TryGetRowIdAliasPk(out int columnIndex)
    {
        columnIndex = -1;
        // Avoid the PrimaryKeyColumns throw-on-missing-PK path: alias semantics
        // only apply to tables that actually declare a single-column integer PK.
        SqlColumnDefinition? pkCol = null;
        for (int i = 0; i < Columns.Count; i++)
        {
            if (Columns[i].IsPrimaryKey)
            {
                if (pkCol != null) return false; // multi-column PK
                pkCol = Columns[i];
            }
        }
        if (pkCol == null) return false;
        if (pkCol.Type != SqlScalarType.Int64 && pkCol.Type != SqlScalarType.Int32) return false;
        for (int i = 0; i < Columns.Count; i++)
        {
            if (ReferenceEquals(Columns[i], pkCol))
            {
                columnIndex = i;
                return true;
            }
        }
        return false;
    }
}
