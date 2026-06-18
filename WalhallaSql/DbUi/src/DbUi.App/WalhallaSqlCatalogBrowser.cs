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
        ];
    }

    private IReadOnlyList<CatalogNode> GetFolderChildren(string[] parts)
    {
        if (parts.Length < 3)
            return [];

        var database = parts[1];
        var folderKind = parts[2];
        var engine = _engineProvider();

        return folderKind switch
        {
            "tables" => engine.GetAllTables()
                .OrderBy(static t => t.CollectionName, StringComparer.OrdinalIgnoreCase)
                .Select(table =>
                    new CatalogNode(
                        CreateId("table", database, table.CollectionName),
                        $"dbo.{table.CollectionName}",
                        CatalogNodeKind.Table,
                        HasChildren: table.Columns.Count > 0,
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
                        }))
                .ToArray(),
            "views" => [],
            _ => [],
        };
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

        return table.Columns
            .Select(column =>
                new CatalogNode(
                    CreateId("column", database, table.CollectionName, column.Name),
                    $"{column.Name} ({FormatType(column.Type)})",
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

    private static CatalogNode CreateFolderNode(string database, string folderKind, string displayName) =>
        new(
            CreateId("folder", database, folderKind),
            displayName,
            CatalogNodeKind.Folder,
            HasChildren: true,
            Metadata: new Dictionary<string, string?>
            {
                ["database"] = database,
                ["folderKind"] = folderKind,
            });

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
