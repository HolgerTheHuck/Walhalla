using System;
using System.Linq;
using WalhallaSql.Sql;

namespace WalhallaSql.EfCore.Migrations;

/// <summary>
/// Builds SQL DDL statements for WalhallaSql migration operations.
/// Used by <see cref="WalhallaSqlMigrationService"/>, <see cref="WalhallaSqlMigrator"/>,
/// and <see cref="WalhallaSqlDatabaseCreator"/> to render migration plans as SQL scripts.
/// </summary>
public static class WalhallaSqlMigrationScriptBuilder
{
    public static string BuildMigrationSql(MigrationOperation operation)
    {
        return operation switch
        {
            CreateTableOperation createTable => BuildCreateTableSql(createTable.Table),
            RenameTableOperation renameTable => $"ALTER TABLE {renameTable.CollectionName} RENAME TO {renameTable.NewCollectionName}",
            DropTableOperation dropTable => $"DROP TABLE {dropTable.CollectionName}",
            AddColumnOperation addColumn => BuildAddColumnSql(addColumn),
            RenameColumnOperation renameColumn => $"ALTER TABLE {renameColumn.CollectionName} RENAME COLUMN {renameColumn.OldColumnName} TO {renameColumn.NewColumnName}",
            DropColumnOperation dropColumn => $"ALTER TABLE {dropColumn.CollectionName} DROP COLUMN {dropColumn.ColumnName}",
            AlterColumnOperation alterColumn => $"ALTER TABLE {alterColumn.CollectionName} ALTER COLUMN {alterColumn.Column.Name} TYPE {MapTypeSql(alterColumn.Column.Type)}{(alterColumn.Column.IsNullable && !alterColumn.Column.IsPrimaryKey ? " NULL" : " NOT NULL")}",
            CreateIndexOperation createIndex => createIndex.Index.IsUnique
                ? $"CREATE UNIQUE INDEX {createIndex.Index.IndexName} ON {createIndex.CollectionName} ({string.Join(", ", createIndex.Index.ColumnNames)})"
                : $"CREATE INDEX {createIndex.Index.IndexName} ON {createIndex.CollectionName} ({string.Join(", ", createIndex.Index.ColumnNames)})",
            DropIndexOperation dropIndex => $"DROP INDEX {dropIndex.IndexName} ON {dropIndex.CollectionName}",
            AddForeignKeyOperation addForeignKey => BuildAddForeignKeySql(addForeignKey),
            DropForeignKeyOperation dropForeignKey => $"ALTER TABLE {dropForeignKey.CollectionName} DROP CONSTRAINT {dropForeignKey.ConstraintName}",
            _ => throw new NotSupportedException($"Migration operation '{operation.GetType().Name}' is not supported.")
        };
    }

    public static string BuildCreateTableSql(SqlTableDefinition table)
    {
        var columns = string.Join(", ", table.Columns.Select(column => BuildColumnDefinitionSql(column, includePrimaryKey: true, includeUnique: true)));
        return $"CREATE TABLE {table.CollectionName} ({columns})";
    }

    public static string BuildAddColumnSql(AddColumnOperation operation)
    {
        var sql = $"ALTER TABLE {operation.CollectionName} ADD COLUMN {BuildColumnDefinitionSql(operation.Column, includePrimaryKey: false, includeUnique: true)}";
        if (!string.IsNullOrWhiteSpace(operation.DefaultValueLiteral))
            sql += $" DEFAULT {operation.DefaultValueLiteral}";

        return sql;
    }

    public static string BuildAddForeignKeySql(AddForeignKeyOperation operation)
    {
        var fk = operation.ForeignKey;
        var childColumns = string.Join(", ", fk.ColumnNames);
        var refColumns = string.Join(", ", fk.ReferencedColumns);
        return $"ALTER TABLE {operation.CollectionName} ADD CONSTRAINT {fk.ConstraintName} FOREIGN KEY ({childColumns}) REFERENCES {fk.ReferencedCollection} ({refColumns}) ON DELETE {MapForeignKeyActionSql(fk.OnDelete)} ON UPDATE {MapForeignKeyActionSql(fk.OnUpdate)}";
    }

    public static string MapForeignKeyActionSql(SqlForeignKeyAction action)
    {
        return action switch
        {
            SqlForeignKeyAction.Cascade => "CASCADE",
            SqlForeignKeyAction.SetNull => "SET NULL",
            _ => "RESTRICT"
        };
    }

    public static string MapTypeSql(SqlScalarType type)
    {
        return type switch
        {
            SqlScalarType.Int32 => "INT",
            SqlScalarType.Int64 => "BIGINT",
            SqlScalarType.Double => "DOUBLE",
            SqlScalarType.Decimal => "DECIMAL",
            SqlScalarType.Boolean => "BIT",
            SqlScalarType.DateTime => "DATETIME",
            SqlScalarType.Guid => "GUID",
            SqlScalarType.Binary => "VARBINARY",
            SqlScalarType.String => "TEXT",
            _ => "TEXT"
        };
    }

    public static string BuildColumnDefinitionSql(SqlColumnDefinition column, bool includePrimaryKey, bool includeUnique)
    {
        var typeSql = MapTypeSql(column.Type);
        var nullabilitySql = column.IsNullable && !column.IsPrimaryKey ? string.Empty : " NOT NULL";
        var primaryKeySql = includePrimaryKey && column.IsPrimaryKey ? " PRIMARY KEY" : string.Empty;
        var uniqueSql = includeUnique && !column.IsPrimaryKey && column.IsUnique ? " UNIQUE" : string.Empty;
        return $"{column.Name} {typeSql}{nullabilitySql}{primaryKeySql}{uniqueSql}";
    }
}
