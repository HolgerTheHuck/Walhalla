using System.Data;
using System.Data.Common;
using DbUi.Core.Catalog;

namespace DbUi.App;

public sealed class PgWireCatalogBrowser : ICatalogBrowser
{
    private readonly Func<DbConnection> _connectionProvider;
    private readonly string _connectionDisplayName;

    public PgWireCatalogBrowser(Func<DbConnection> connectionProvider, string connectionDisplayName)
    {
        _connectionProvider = connectionProvider;
        _connectionDisplayName = connectionDisplayName;
    }

    public Task<IReadOnlyList<CatalogNode>> GetRootNodesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<CatalogNode> roots =
        [
            new CatalogNode(
                CreateId("server", _connectionDisplayName),
                _connectionDisplayName,
                CatalogNodeKind.Server,
                HasChildren: true),
        ];

        return Task.FromResult(roots);
    }

    public async Task<IReadOnlyList<CatalogNode>> GetChildrenAsync(
        CatalogNodeId nodeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var parts = ParseId(nodeId);
        if (parts.Length == 0)
            return [];

        return parts[0] switch
        {
            "server" => await GetDatabaseNodesAsync(cancellationToken),
            "database" => GetDatabaseChildren(parts),
            "folder" => await GetFolderChildrenAsync(parts, cancellationToken),
            "table" => await GetTableChildrenAsync(parts, cancellationToken),
            _ => [],
        };
    }

    public async Task<CatalogSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tables = await GetTablesAsync(cancellationToken);
        var procedures = await GetRoutinesAsync(cancellationToken);

        return new CatalogSnapshot(tables, procedures);
    }

    private async Task<IReadOnlyList<CatalogNode>> GetDatabaseNodesAsync(
        CancellationToken cancellationToken)
    {
        var databases = new List<CatalogNode>();
        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            databases.Add(new CatalogNode(
                CreateId("database", name),
                name,
                CatalogNodeKind.Database,
                HasChildren: true,
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = name,
                    ["isCurrent"] = bool.FalseString,
                }));
        }

        return databases;
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

    private async Task<IReadOnlyList<CatalogNode>> GetFolderChildrenAsync(
        string[] parts,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 3)
            return [];

        var database = parts[1];
        var folderKind = parts[2];

        if (parts.Length == 3)
        {
            return folderKind switch
            {
                "tables" => await GetTableNodesAsync(database, cancellationToken),
                "views" => await GetViewNodesAsync(database, cancellationToken),
                "routines" => await GetRoutineNodesAsync(database, cancellationToken),
                _ => [],
            };
        }

        if (parts.Length < 4)
            return [];

        var tableName = parts[2];
        var nestedFolderKind = parts[3];

        var columns = await GetTableColumnsAsync(tableName, cancellationToken);

        return nestedFolderKind switch
        {
            "columns" => columns.Select(c => new CatalogNode(
                CreateId("column", database, tableName, c),
                c,
                CatalogNodeKind.Column,
                HasChildren: false)).ToArray(),
            "keys" => [],
            "foreignkeys" => [],
            "indexes" => [],
            _ => [],
        };
    }

    private async Task<IReadOnlyList<CatalogNode>> GetTableNodesAsync(
        string database,
        CancellationToken cancellationToken)
    {
        var tables = new List<CatalogNode>();
        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
            ORDER BY table_name
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tableName = reader.GetString(0);
            tables.Add(new CatalogNode(
                CreateId("table", database, tableName),
                $"dbo.{tableName}",
                CatalogNodeKind.Table,
                HasChildren: true,
                Actions:
                [
                    new CatalogAction("select-top", "SELECT TOP (1000) *", BuildSelectTop(tableName)),
                    new CatalogAction("select-all", "SELECT *", BuildSelectAll(tableName)),
                    new CatalogAction("count", "SELECT COUNT(*)", BuildCount(tableName)),
                ],
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = database,
                    ["objectName"] = tableName,
                }));
        }

        return tables;
    }

    private async Task<IReadOnlyList<CatalogNode>> GetViewNodesAsync(
        string database,
        CancellationToken cancellationToken)
    {
        var views = new List<CatalogNode>();
        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT table_name
            FROM information_schema.views
            WHERE table_schema = 'public'
            ORDER BY table_name
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var viewName = reader.GetString(0);
            views.Add(new CatalogNode(
                CreateId("view", database, viewName),
                viewName,
                CatalogNodeKind.View,
                HasChildren: false,
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = database,
                    ["objectName"] = viewName,
                }));
        }

        return views;
    }

    private async Task<IReadOnlyList<CatalogNode>> GetRoutineNodesAsync(
        string database,
        CancellationToken cancellationToken)
    {
        var routines = new List<CatalogNode>();
        var parametersByRoutine = await GetParametersByRoutineAsync(cancellationToken);

        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT routine_name, routine_type
            FROM information_schema.routines
            WHERE routine_schema = 'public'
            ORDER BY routine_name
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            parametersByRoutine.TryGetValue(name, out var parameters);

            routines.Add(new CatalogNode(
                CreateId("routine", database, name),
                $"{name} ({type})",
                CatalogNodeKind.Routine,
                HasChildren: false,
                Actions:
                [
                    new CatalogAction("exec", "EXEC", BuildExecProcedure(name, parameters ?? [])),
                    new CatalogAction("drop", "DROP", $"DROP {type} {EscapeName(name)}"),
                ],
                Metadata: new Dictionary<string, string?>
                {
                    ["database"] = database,
                    ["objectName"] = name,
                }));
        }

        return routines;
    }

    private async Task<Dictionary<string, List<(string Name, string Type)>>> GetParametersByRoutineAsync(
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.OrdinalIgnoreCase);
        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT specific_name, parameter_name, data_type
            FROM information_schema.parameters
            ORDER BY specific_name, ordinal_position
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var routineName = reader.GetString(0);
            var paramName = reader.GetString(1);
            var dataType = reader.GetString(2);

            if (!result.TryGetValue(routineName, out var parameters))
            {
                parameters = [];
                result[routineName] = parameters;
            }

            parameters.Add((paramName, dataType));
        }

        return result;
    }

    private static string BuildExecProcedure(string name, IReadOnlyList<(string Name, string Type)> parameters)
    {
        var args = string.Join(", ", parameters.Select(p => $"@{p.Name}"));
        return $"EXEC {EscapeName(name)} {args}".TrimEnd();
    }

    private async Task<IReadOnlyList<CatalogNode>> GetTableChildrenAsync(
        string[] parts,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 3)
            return [];

        var database = parts[1];
        var tableName = parts[2];

        return
        [
            CreateFolderNode(database, "columns", "Columns", tableName),
            CreateFolderNode(database, "keys", "Keys", tableName),
            CreateFolderNode(database, "foreignkeys", "Foreign Keys", tableName),
            CreateFolderNode(database, "indexes", "Indexes", tableName),
        ];
    }

    private async Task<IReadOnlyList<CatalogTable>> GetTablesAsync(
        CancellationToken cancellationToken)
    {
        var tables = new List<CatalogTable>();
        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
            ORDER BY table_name, ordinal_position
            """;

        var columnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);

            if (!columnsByTable.TryGetValue(tableName, out var columns))
            {
                columns = [];
                columnsByTable[tableName] = columns;
            }

            columns.Add(columnName);
        }

        return columnsByTable
            .Select(kv => new CatalogTable(kv.Key, kv.Value))
            .ToArray();
    }

    private async Task<IReadOnlyList<CatalogProcedure>> GetRoutinesAsync(
        CancellationToken cancellationToken)
    {
        var parametersByRoutine = await GetParametersByRoutineAsync(cancellationToken);

        var routines = new List<CatalogProcedure>();
        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT routine_name
            FROM information_schema.routines
            WHERE routine_schema = 'public'
            ORDER BY routine_name
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            parametersByRoutine.TryGetValue(name, out var parameters);
            routines.Add(new CatalogProcedure(name, (parameters ?? []).Select(p => $"{p.Name} {p.Type}").ToArray()));
        }

        return routines;
    }

    private async Task<IReadOnlyList<string>> GetTableColumnsAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @tableName
            ORDER BY ordinal_position
            """;

        var param = cmd.CreateParameter();
        param.ParameterName = "@tableName";
        param.Value = tableName;
        cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));

        return columns;
    }

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

    private static CatalogNodeId CreateId(params string[] parts)
    {
        return new CatalogNodeId(string.Join("|", parts.Select(Uri.EscapeDataString)));
    }

    private static string[] ParseId(CatalogNodeId nodeId)
    {
        return nodeId.Value.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
    }

    private static string BuildSelectTop(string objectName)
    {
        return $"SELECT TOP (1000) *{Environment.NewLine}FROM   {BuildObjectName(objectName)}";
    }

    private static string BuildSelectAll(string objectName)
    {
        return $"SELECT *{Environment.NewLine}FROM   {BuildObjectName(objectName)}";
    }

    private static string BuildCount(string objectName)
    {
        return $"SELECT COUNT(*) AS [Count]{Environment.NewLine}FROM   {BuildObjectName(objectName)}";
    }

    private static string BuildObjectName(string objectName)
    {
        return $"[{EscapeName(objectName)}]";
    }

    private static string EscapeName(string value)
    {
        return value.Replace("]", "]]", StringComparison.Ordinal);
    }
}
