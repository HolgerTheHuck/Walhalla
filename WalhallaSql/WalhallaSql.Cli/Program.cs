using WalhallaSql.Cli;
using WalhallaSql.Cli.Importers;

var options = MigratorOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(MigratorOptions.HelpText);
    return;
}

if (!options.IsValid(out var validationError))
{
    Console.Error.WriteLine(validationError);
    Console.WriteLine(MigratorOptions.HelpText);
    Environment.ExitCode = 2;
    return;
}

if (string.Equals(options.Mode, "sqlite", StringComparison.OrdinalIgnoreCase))
{
    var importer = new SqliteImporter();
    var request = new SqliteMigrationRequest
    {
        SourcePath = options.SqliteSourcePath!,
        TargetPath = options.SqliteTargetPath!,
        TargetDatabase = options.SqliteTargetDatabase,
        BatchSize = options.BatchSize,
        DropAndRecreate = options.DropAndRecreate,
        Tables = options.Tables.Count > 0 ? options.Tables : null
    };

    var result = await importer.MigrateAsync(request, new Progress<string>(message => Console.WriteLine(message)));
    if (!result.Success)
    {
        var failure = result.Tables.LastOrDefault(table => !table.Success);
        if (failure?.Error is not null)
            Console.Error.WriteLine(failure.Error);

        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("SQLite migration finished successfully.");
    return;
}

// Default: MSSQL mode
var migrator = new MssqlMigrationService();
var requestMssql = new MssqlMigrationRequest(
    options.MssqlConnectionString,
    options.WalhallaPath,
    options.WalhallaDatabase,
    options.SourceSchema,
    options.Tables,
    options.BatchSize,
    options.DropAndRecreate);

var resultMssql = await migrator.MigrateAsync(requestMssql, new Progress<string>(message => Console.WriteLine(message)));
if (!resultMssql.Success)
{
    var failure = resultMssql.Tables.LastOrDefault(table => !table.Success);
    if (failure?.Error is not null)
        Console.Error.WriteLine(failure.Error);

    Environment.ExitCode = 1;
    return;
}

Console.WriteLine("MSSQL migration finished successfully.");

internal sealed record MigratorOptions(
    string Mode,
    string MssqlConnectionString,
    string WalhallaPath,
    string WalhallaDatabase,
    string SourceSchema,
    string SqliteSourcePath,
    string SqliteTargetPath,
    string SqliteTargetDatabase,
    IReadOnlyList<string> Tables,
    int BatchSize,
    bool DropAndRecreate,
    bool ShowHelp)
{
    public static string HelpText => """
walhallactl - WalhallaSql Database Import Tool

Usage (MSSQL mode):
  walhallactl \
    --mode mssql \
    --mssql "Server=.;Database=App;Trusted_Connection=True;TrustServerCertificate=True" \
    --walhalla-path "E:\Data\WalhallaSnapshot" \
    --walhalla-database "App" \
    --schema "dbo" \
    --tables "Users,Orders,Categories" \
    --batch-size 500 \
    --drop-and-recreate true

Usage (SQLite mode):
  walhallactl \
    --mode sqlite \
    --sqlite-source "E:\Data\source.db" \
    --sqlite-output "E:\Data\WalhallaSnapshot" \
    --sqlite-database "App" \
    --tables "Users,Orders" \
    --batch-size 5000 \
    --drop-and-recreate true

Required (MSSQL):
  --mssql              SQL Server connection string
  --walhalla-path      target storage path
  --tables             comma-separated table list

Required (SQLite):
  --sqlite-source      path to source SQLite database file
  --sqlite-output      target Walhalla storage path

Optional (both):
  --mode               migration mode: mssql (default) or sqlite
  --tables             comma-separated table list (default: all tables)
  --batch-size         default: 500
  --drop-and-recreate  default: false
  --help               show help
""";

    public static MigratorOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = token[2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";

            values[key] = value;
        }

        var tableValue = values.TryGetValue("tables", out var rawTables) ? rawTables : string.Empty;
        var tables = tableValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var showHelp = values.ContainsKey("help") || values.ContainsKey("h") || args.Length == 0;
        var batchSize = values.TryGetValue("batch-size", out var batchRaw) && int.TryParse(batchRaw, out var parsedBatch)
            ? Math.Max(1, parsedBatch)
            : 500;

        var dropAndRecreate = values.TryGetValue("drop-and-recreate", out var dropRaw) && bool.TryParse(dropRaw, out var dropParsed)
            ? dropParsed
            : false;

        var mode = values.TryGetValue("mode", out var modeRaw) ? modeRaw : "mssql";

        return new MigratorOptions(
            Mode: mode,
            MssqlConnectionString: values.TryGetValue("mssql", out var mssql) ? mssql : string.Empty,
            WalhallaPath: values.TryGetValue("walhalla-path", out var path) ? path : string.Empty,
            WalhallaDatabase: values.TryGetValue("walhalla-database", out var database) ? database : "App",
            SourceSchema: values.TryGetValue("schema", out var schema) ? schema : "dbo",
            SqliteSourcePath: values.TryGetValue("sqlite-source", out var sqliteSource) ? sqliteSource : string.Empty,
            SqliteTargetPath: values.TryGetValue("sqlite-output", out var sqliteOutput) ? sqliteOutput : string.Empty,
            SqliteTargetDatabase: values.TryGetValue("sqlite-database", out var sqliteDatabase) ? sqliteDatabase : "App",
            Tables: tables,
            BatchSize: batchSize,
            DropAndRecreate: dropAndRecreate,
            ShowHelp: showHelp);
    }

    public bool IsValid(out string? error)
    {
        if (ShowHelp)
        {
            error = null;
            return true;
        }

        if (string.Equals(Mode, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(SqliteSourcePath))
            {
                error = "Missing --sqlite-source path.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SqliteTargetPath))
            {
                error = "Missing --sqlite-output path.";
                return false;
            }

            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(MssqlConnectionString))
        {
            error = "Missing --mssql connection string.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(WalhallaPath))
        {
            error = "Missing --walhalla-path.";
            return false;
        }

        if (Tables.Count == 0)
        {
            error = "Missing --tables list.";
            return false;
        }

        error = null;
        return true;
    }
}
