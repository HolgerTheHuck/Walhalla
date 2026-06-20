using DbUi.Core.Catalog;
using System.IO;
using WalhallaSql;
using WalhallaSql.Sql;

namespace DbUi.App;

public sealed class WalhallaSqlCatalogBrowser : ICatalogBrowser
{
    private readonly Func<WalhallaEngine> _engineProvider;
    private readonly Func<string> _databaseNameProvider;
    private readonly Func<string> _storagePathProvider;

    public WalhallaSqlCatalogBrowser(
        Func<WalhallaEngine> engineProvider,
        Func<string> databaseNameProvider,
        Func<string> storagePathProvider)
    {
        _engineProvider = engineProvider;
        _databaseNameProvider = databaseNameProvider;
        _storagePathProvider = storagePathProvider;
    }

    public Task<IReadOnlyList<CatalogNode>> GetRootNodesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var storagePath = _storagePathProvider();
        IReadOnlyList<CatalogNode> roots =
        [
            new CatalogNode(
                CreateId("server", storagePath),
                BuildStorageDisplayName(storagePath),
                CatalogNodeKind.Server,
                HasChildren: true,
                Metadata: new Dictionary<string, string?>
                {
                    ["storagePath"] = storagePath,
                }),
        ];

        return Task.FromResult(roots);
    }

    public Task<IReadOnlyList<CatalogNode>> GetChildrenAsync(
        CatalogNodeId nodeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var parts = ParseId(nodeId);
        if (parts.Length == 0)
            return Task.FromResult<IReadOnlyList<CatalogNode>>([]);

        return parts[0] switch
        {
            "server" => Task.FromResult(GetDatabaseNodes()),
            "database" => Task.FromResult(GetDatabaseChildren(parts)),
            "folder" => Task.FromResult(GetFolderChildren(parts)),
            "table" => Task.FromResult(GetTableChildren(parts)),
            _ => Task.FromResult<IReadOnlyList<CatalogNode>>([]),
        };
    }

    public Task<CatalogSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var engine = _engineProvider();
        var tables = engine.GetAllTables()
            .Select(t => new CatalogTable(
                t.CollectionName,
                t.Columns.Select(c => c.Name).ToArray()))
            .ToArray();

        var procedures = engine.GetProcedures()
            .Select(p => new CatalogProcedure(
                p.Name,
                p.Parameters.Select(par => $"{par.Name} {par.Type}").ToArray()))
            .ToArray();

        return Task.FromResult(new CatalogSnapshot(tables, procedures));
    }

    private IReadOnlyList<CatalogNode> GetDatabaseNodes()
    {
        var database = _databaseNameProvider();
        return
        [
            new CatalogNode(
                CreateId("database", database),
                database,
                CatalogNodeKind.Database,
                HasChildren: true,
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = database,
                    ["isCurrent"] = bool.TrueString,
                }),
        ];
    }

    private static IReadOnlyList<CatalogNode> GetDatabaseChildren(string[] parts)
    {
        if (parts.Length < 2)
            return [];

        var database = parts[1];
        return
        [
            CreateFolderNode(database, "tables", "Tables"),
            CreateFolderNode(database, "views", "Views"),
            CreateFolderNode(database, "routines", "Routines"),
        ];
    }

    private IReadOnlyList<CatalogNode> GetFolderChildren(string[] parts)
    {
        if (parts.Length < 3)
            return [];

        var database = parts[1];
        var folderKind = parts[2];
        var engine = _engineProvider();

        // Top-level folder under database (e.g. "tables", "views", "routines")
        if (parts.Length == 3)
        {
            return folderKind switch
            {
                "tables" => engine.GetAllTables()
                    .OrderBy(static t => t.CollectionName, StringComparer.OrdinalIgnoreCase)
                    .Select(table =>
                        new CatalogNode(
                            CreateId("table", database, table.CollectionName),
                            $"dbo.{table.CollectionName}",
                            CatalogNodeKind.Table,
                            HasChildren: true,
                            Actions:
                            [
                                new CatalogAction("select-top", "SELECT TOP (1000) *", BuildSelectTop(table.CollectionName)),
                                new CatalogAction("select-all", "SELECT *", BuildSelectAll(table.CollectionName)),
                                new CatalogAction("count", "SELECT COUNT(*)", BuildCount(table.CollectionName)),
                            ],
                            Metadata: new Dictionary<string, string?>
                            {
                                ["database"] = database,
                                ["objectName"] = table.CollectionName,
                                ["columns"] = string.Join(",", table.Columns.Select(c => c.Name)),
                            }))
                    .ToArray(),
                "views" => [],
                "routines" => GetRoutineNodes(database, engine),
                _ => [],
            };
        }

        // Nested folder under a table (parts: "folder"|database|tableName|folderKind)
        if (parts.Length < 4)
            return [];

        var tableName = parts[2];
        var nestedFolderKind = parts[3];
        var table = engine.GetTableDefinition(tableName);
        if (table is null)
            return [];

        return nestedFolderKind switch
        {
            "columns" => GetColumnNodes(database, table),
            "keys" => GetKeyNodes(database, table),
            "foreignkeys" => GetForeignKeyNodes(database, table),
            "indexes" => GetIndexNodes(database, table),
            "triggers" => GetTriggerNodes(database, table.CollectionName, engine),
            _ => [],
        };
    }

    private static IReadOnlyList<CatalogNode> GetRoutineNodes(string database, WalhallaEngine engine)
    {
        return engine.GetProcedures()
            .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(proc => new CatalogNode(
                CreateId("routine", database, proc.Name),
                $"{proc.Name} ({proc.Language})",
                CatalogNodeKind.Routine,
                HasChildren: false,
                Actions:
                [
                    new CatalogAction("exec", "EXEC", BuildExecProcedure(proc.Name, proc.Parameters)),
                    new CatalogAction("alter", "ALTER", BuildAlterProcedure(proc.Name, proc.Parameters, proc.Body, proc.Language)),
                    new CatalogAction("drop", "DROP", $"DROP PROCEDURE {EscapeName(proc.Name)}"),
                ],
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = database,
                    ["objectName"] = proc.Name,
                    ["language"] = proc.Language,
                }))
            .ToArray();
    }

    private static IReadOnlyList<CatalogNode> GetTriggerNodes(string database, string tableName, WalhallaEngine engine)
    {
        return engine.GetTriggers(tableName)
            .OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(trigger => new CatalogNode(
                CreateId("trigger", database, tableName, trigger.Name),
                $"{trigger.Name} ({trigger.Timing} {trigger.Event})",
                CatalogNodeKind.Trigger,
                HasChildren: false,
                Actions:
                [
                    new CatalogAction("edit", "EDIT", BuildEditTrigger(trigger)),
                    new CatalogAction("drop", "DROP", $"DROP TRIGGER {EscapeName(trigger.Name)}"),
                ],
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = database,
                    ["objectName"] = trigger.Name,
                    ["tableName"] = tableName,
                }))
            .ToArray();
    }

    private static string BuildEditTrigger(SqlTriggerDefinition trigger)
    {
        return $"CREATE OR REPLACE TRIGGER {EscapeName(trigger.Name)}\n"
            + $"ON {EscapeName(trigger.TableName)} {trigger.Timing.ToString().ToUpperInvariant()} {trigger.Event.ToString().ToUpperInvariant()} AS\n"
            + $"BEGIN\n{trigger.Body}\nEND";
    }

    private static string BuildExecProcedure(string name, IReadOnlyList<SqlProcedureParameter> parameters)
    {
        var args = string.Join(", ", parameters.Select(p => $"@{p.Name}"));
        return $"EXEC {EscapeName(name)} {args}".TrimEnd();
    }

    private static string BuildAlterProcedure(string name, IReadOnlyList<SqlProcedureParameter> parameters, string body, string language)
    {
        var paramList = string.Join(", ", parameters.Select(p => $"@{p.Name} {FormatType(p.Type)}"));
        var langClause = string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase) ? " LANGUAGE CSHARP" : "";
        return $"CREATE OR REPLACE PROCEDURE {EscapeName(name)}({paramList}){langClause}{Environment.NewLine}AS{Environment.NewLine}{body}";
    }

    private static IReadOnlyList<CatalogNode> GetColumnNodes(string database, SqlTableDefinition table)
    {
        return table.Columns
            .Select(column =>
                new CatalogNode(
                    CreateId("column", database, table.CollectionName, column.Name),
                    FormatColumnDisplay(column),
                    CatalogNodeKind.Column,
                    HasChildren: false,
                    Metadata: new Dictionary<string, string?>
                    {
                        ["database"] = database,
                        ["objectName"] = table.CollectionName,
                        ["columnName"] = column.Name,
                    }))
            .ToArray();
    }

    private static string FormatColumnDisplay(SqlColumnDefinition column)
    {
        var markers = new List<string>();
        if (column.IsPrimaryKey) markers.Add("PK");
        if (column.IsUnique) markers.Add("UQ");
        var suffix = markers.Count > 0 ? $" [{string.Join(", ", markers)}]" : "";
        return $"{column.Name} ({FormatType(column.Type)}){suffix}";
    }

    private static IReadOnlyList<CatalogNode> GetKeyNodes(string database, SqlTableDefinition table)
    {
        var nodes = new List<CatalogNode>();

        var pkColumns = table.PrimaryKeyColumns.Select(c => c.Name).ToArray();
        if (pkColumns.Length > 0)
        {
            var pkName = pkColumns.Length == 1 ? $"PK_{table.CollectionName}" : "PRIMARY KEY";
            nodes.Add(new CatalogNode(
                CreateId("primarykey", database, table.CollectionName, pkName),
                $"PRIMARY KEY ({string.Join(", ", pkColumns)})",
                CatalogNodeKind.PrimaryKey,
                HasChildren: false,
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = database,
                    ["objectName"] = table.CollectionName,
                }));
        }

        var uniqueColumns = table.Columns.Where(c => c.IsUnique && !c.IsPrimaryKey).ToArray();
        foreach (var column in uniqueColumns)
        {
            nodes.Add(new CatalogNode(
                CreateId("constraint", database, table.CollectionName, $"UQ_{column.Name}"),
                $"UNIQUE ({column.Name})",
                CatalogNodeKind.Constraint,
                HasChildren: false,
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = database,
                    ["objectName"] = table.CollectionName,
                    ["columnName"] = column.Name,
                }));
        }

        return nodes;
    }

    private static IReadOnlyList<CatalogNode> GetForeignKeyNodes(string database, SqlTableDefinition table)
    {
        if (table.ForeignKeys is null || table.ForeignKeys.Count == 0)
            return [];

        return table.ForeignKeys
            .Select(fk =>
                new CatalogNode(
                    CreateId("foreignkey", database, table.CollectionName, fk.ConstraintName),
                    $"{fk.ConstraintName}: ({string.Join(", ", fk.ColumnNames)}) → {fk.ReferencedCollection}({string.Join(", ", fk.ReferencedColumns)})",
                    CatalogNodeKind.ForeignKey,
                    HasChildren: false,
                    Metadata: new Dictionary<string, string?>
                    {
                        ["database"] = database,
                        ["objectName"] = table.CollectionName,
                        ["constraintName"] = fk.ConstraintName,
                    }))
            .ToArray();
    }

    private static IReadOnlyList<CatalogNode> GetIndexNodes(string database, SqlTableDefinition table)
    {
        if (table.Indexes.Count == 0)
            return [];

        return table.Indexes
            .Where(index => !index.IsInternal)
            .Select(index =>
                new CatalogNode(
                    CreateId("index", database, table.CollectionName, index.IndexName),
                    FormatIndexDisplay(index),
                    CatalogNodeKind.Index,
                    HasChildren: false,
                    Actions:
                    [
                        new CatalogAction("drop", "DROP INDEX", $"DROP INDEX {EscapeName(index.IndexName)} ON {BuildObjectName(table.CollectionName)}"),
                    ],
                    Metadata: new Dictionary<string, string?>
                    {
                        ["database"] = database,
                        ["objectName"] = table.CollectionName,
                        ["indexName"] = index.IndexName,
                    }))
            .ToArray();
    }

    private static string FormatIndexDisplay(SqlIndexDefinition index)
    {
        var columns = index.TargetsProjection
            ? $"projection {index.TargetProjectionName}"
            : string.Join(", ", index.ColumnNames);
        var unique = index.IsUnique ? "UNIQUE " : "";
        var type = index.IndexType == SqlIndexType.Gin ? "GIN " : "";
        return $"{unique}{type}{index.IndexName} ({columns})";
    }

    private IReadOnlyList<CatalogNode> GetTableChildren(string[] parts)
    {
        if (parts.Length < 3)
            return [];

        var database = parts[1];
        var tableName = parts[2];
        var engine = _engineProvider();
        var table = engine.GetTableDefinition(tableName);
        if (table is null)
            return [];

        var tableId = table.CollectionName;
        return
        [
            CreateFolderNode(database, "columns", "Columns", tableId),
            CreateFolderNode(database, "keys", "Keys", tableId),
            CreateFolderNode(database, "foreignkeys", "Foreign Keys", tableId),
            CreateFolderNode(database, "indexes", "Indexes", tableId),
            CreateFolderNode(database, "triggers", "Triggers", tableId),
        ];
    }

    private static string FormatType(SqlScalarType type) => type switch
    {
        SqlScalarType.Int32 => "INT",
        SqlScalarType.Int64 => "BIGINT",
        SqlScalarType.Int16 => "SMALLINT",
        SqlScalarType.Double => "FLOAT",
        SqlScalarType.Decimal => "DECIMAL",
        SqlScalarType.String => "NVARCHAR",
        SqlScalarType.Boolean => "BIT",
        SqlScalarType.DateTime => "DATETIME",
        SqlScalarType.Date => "DATE",
        SqlScalarType.Time => "TIME",
        SqlScalarType.Binary => "VARBINARY",
        SqlScalarType.Guid => "UNIQUEIDENTIFIER",
        SqlScalarType.Json => "NVARCHAR(MAX)",
        SqlScalarType.Geometry => "GEOMETRY",
        _ => "UNKNOWN",
    };

    private static CatalogNode CreateFolderNode(
        string database, string folderKind, string displayName, string? tableName = null)
    {
        var idParts = tableName is null
            ? new[] { "folder", database, folderKind }
            : new[] { "folder", database, tableName, folderKind };

        var metadata = new Dictionary<string, string?>
        {
            ["database"] = database,
            ["folderKind"] = folderKind,
        };
        if (tableName is not null)
            metadata["objectName"] = tableName;

        return new CatalogNode(
            CreateId(idParts),
            displayName,
            CatalogNodeKind.Folder,
            HasChildren: true,
            Metadata: metadata);
    }

    private static CatalogNodeId CreateId(params string[] parts) =>
        new(string.Join("|", parts.Select(Uri.EscapeDataString)));

    private static string[] ParseId(CatalogNodeId nodeId) =>
        nodeId.Value.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

    private static string BuildSelectTop(string objectName) =>
        $"SELECT TOP (1000) *{Environment.NewLine}FROM   {BuildObjectName(objectName)}";

    private static string BuildSelectAll(string objectName) =>
        $"SELECT *{Environment.NewLine}FROM   {BuildObjectName(objectName)}";

    private static string BuildCount(string objectName) =>
        $"SELECT COUNT(*) AS [Count]{Environment.NewLine}FROM   {BuildObjectName(objectName)}";

    private static string BuildObjectName(string objectName) =>
        $"[{EscapeName(objectName)}]";

    private static string EscapeName(string value) => value.Replace("]", "]]", StringComparison.Ordinal);

    private static string BuildStorageDisplayName(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath) || storagePath == ":memory:")
            return "WalhallaSql (In-Memory)";

        return $"WalhallaSql ({Path.GetFileName(storagePath)})";
    }
}
