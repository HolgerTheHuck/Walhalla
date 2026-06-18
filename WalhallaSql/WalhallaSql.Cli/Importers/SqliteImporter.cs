using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using WalhallaSql.AdoNet;

namespace WalhallaSql.Cli.Importers;

/// <summary>
/// One-shot migration from a SQLite database to a WalhallaSql embedded database.
/// </summary>
public sealed class SqliteImporter
{
    public Task<SqliteMigrationResult> MigrateAsync(SqliteMigrationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Migrate(request, progress, cancellationToken), cancellationToken);

    public SqliteMigrationResult Migrate(SqliteMigrationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var startedAtUtc = DateTime.UtcNow;
        var normalizedTables = request.Tables?.Count > 0
            ? request.Tables
                .Where(static t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        Directory.CreateDirectory(request.TargetPath);

        using var sourceConnection = new SqliteConnection($"Data Source={request.SourcePath};Mode=ReadOnly");
        sourceConnection.Open();

        using var engine = WalhallaEngine.Open(request.TargetPath);
        using var targetConnection = new WalhallaSqlDbConnection(engine, $"DataSource=embedded;Database={request.TargetDatabase}");
        targetConnection.Open();

        progress?.Report($"Source: SQLite ({request.SourcePath})");
        progress?.Report($"Target: Walhalla ({request.TargetPath})");
        progress?.Report(string.Empty);

        // If no explicit tables, discover all user tables.
        var tablesToMigrate = normalizedTables.Length > 0
            ? normalizedTables
            : DiscoverUserTables(sourceConnection);

        progress?.Report($"Tables: {string.Join(", ", tablesToMigrate)}");
        progress?.Report(string.Empty);

        var tableResults = new List<SqliteMigrationTableResult>(tablesToMigrate.Count);

        foreach (var table in tablesToMigrate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tableStartedAtUtc = DateTime.UtcNow;

            try
            {
                progress?.Report($"[{table}] loading schema...");
                var schema = LoadTableSchema(sourceConnection, table);
                if (schema.Columns.Count == 0)
                    throw new InvalidOperationException($"Table '{table}' has no readable columns.");

                if (request.DropAndRecreate)
                    TryDropTable(targetConnection, table);

                var createSql = BuildCreateTableSql(schema);
                try
                {
                    ExecuteNonQuery(targetConnection, createSql);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to create target table '{schema.TableName}'. SQL: {createSql}", ex);
                }

                var copyResult = CopyRows(sourceConnection, targetConnection, schema, request.BatchSize, progress, cancellationToken);
                var importedRows = copyResult.ImportedRows;
                var sourceCount = CountSourceRows(sourceConnection, schema.TableName);
                var targetCount = CountTargetRows(targetConnection, schema.TableName);
                var duration = DateTime.UtcNow - tableStartedAtUtc;
                var errors = new List<string>();
                if (copyResult.FailedRows > 0)
                    errors.Add($"Skipped {copyResult.FailedRows} rows. First error: {copyResult.FirstError}");

                if (sourceCount != targetCount || importedRows != targetCount)
                    errors.Add($"Row count mismatch for table '{table}'. source={sourceCount}, target={targetCount}, imported={importedRows}");

                var success = errors.Count == 0;
                var error = success ? null : string.Join(" | ", errors);
                var status = success ? "OK" : copyResult.FailedRows > 0 ? "WARN" : "MISMATCH";
                progress?.Report($"[{table}] done in {duration.TotalSeconds:F1}s | imported={importedRows} | skipped={copyResult.FailedRows} | source={sourceCount} | target={targetCount} | {status}");
                progress?.Report(string.Empty);

                tableResults.Add(new SqliteMigrationTableResult(table, importedRows, sourceCount, targetCount, duration, success, error));
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - tableStartedAtUtc;
                var error = DescribeException(ex);
                progress?.Report($"[{table}] failed after {duration.TotalSeconds:F1}s | {error}");
                progress?.Report(string.Empty);
                tableResults.Add(new SqliteMigrationTableResult(table, 0, 0, 0, duration, false, error));
            }
        }

        return new SqliteMigrationResult(tableResults, startedAtUtc, DateTime.UtcNow);
    }

    // ── Discovery ──

    private static IReadOnlyList<string> DiscoverUserTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static TableSchema LoadTableSchema(SqliteConnection connection, string tableName)
    {
        var columns = LoadColumns(connection, tableName).ToList();
        var primaryKeyColumns = LoadPrimaryKeyColumns(connection, tableName);
        var indexes = LoadIndexes(connection, tableName);

        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            columns[i] = col with { IsPrimaryKey = primaryKeyColumns.Contains(col.Name) };
        }

        return new TableSchema(tableName, columns, indexes);
    }

    private static IReadOnlyList<ColumnInfo> LoadColumns(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        var columns = new List<ColumnInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(new ColumnInfo(
                reader.GetInt32(0),               // cid
                reader.GetString(1),              // name
                reader.GetString(2),              // type
                reader.GetInt32(3) != 0,          // notnull
                reader.IsDBNull(4) ? null : reader.GetValue(4), // dflt_value
                reader.GetInt32(5) != 0           // pk
            ));
        }
        return columns;
    }

    private static HashSet<string> LoadPrimaryKeyColumns(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        var pkCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt32(5) != 0) // pk
                pkCols.Add(reader.GetString(1));
        }
        return pkCols;
    }

    private static IReadOnlyList<IndexInfo> LoadIndexes(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)})";
        var indexes = new List<IndexInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var indexName = reader.GetString(1);
            var isUnique = reader.GetInt32(2) != 0;
            var origin = reader.GetString(3);
            if (string.Equals(origin, "pk", StringComparison.OrdinalIgnoreCase))
                continue; // Skip PK indexes, they are inline.

            var columns = new List<string>();
            using var colCmd = connection.CreateCommand();
            colCmd.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)})";
            using var colReader = colCmd.ExecuteReader();
            while (colReader.Read())
            {
                if (!colReader.IsDBNull(2))
                    columns.Add(colReader.GetString(2));
            }

            indexes.Add(new IndexInfo(indexName, columns, isUnique));
        }
        return indexes;
    }

    // ── DDL generation ──

    private static string BuildCreateTableSql(TableSchema schema)
    {
        var pkColumns = schema.Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).ToList();
        var columnDefs = schema.Columns
            .OrderBy(c => c.Ordinal)
            .Select(c => BuildColumnDefinition(c, pkColumns.Count == 1))
            .ToList();

        if (pkColumns.Count > 1)
            columnDefs.Add($"PRIMARY KEY ({string.Join(", ", pkColumns.Select(c => QuoteIdentifier(c.Name)))})");

        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE {QuoteIdentifier(schema.TableName)} ({string.Join(", ", columnDefs)})");
        return sb.ToString();
    }

    private static string BuildColumnDefinition(ColumnInfo column, bool inlinePk)
    {
        var targetType = MapSqliteTypeToWalhalla(column.Type);
        var notNull = column.NotNull || (column.IsPrimaryKey && inlinePk) ? " NOT NULL" : "";
        var pk = column.IsPrimaryKey && inlinePk ? " PRIMARY KEY" : "";
        var defaultSql = column.DefaultValue != null ? $" DEFAULT {FormatDefaultValue(column.DefaultValue)}" : "";
        return $"{QuoteIdentifier(column.Name)} {targetType}{notNull}{pk}{defaultSql}";
    }

    private static string MapSqliteTypeToWalhalla(string? sqliteType)
    {
        if (string.IsNullOrWhiteSpace(sqliteType))
            return "TEXT";

        var t = sqliteType.Trim().ToUpperInvariant();
        var baseType = t.Split('(')[0].Trim();

        return baseType switch
        {
            "INT" or "INTEGER" or "TINYINT" or "SMALLINT" or "MEDIUMINT" => "INT",
            "BIGINT" => "BIGINT",
            "REAL" or "FLOAT" or "DOUBLE" => "DOUBLE",
            "NUMERIC" or "DECIMAL" => ParseDecimalType(sqliteType),
            "BOOLEAN" => "BOOLEAN",
            "DATE" or "DATETIME" or "TIMESTAMP" => "DATETIME",
            "BLOB" => "BINARY",
            "TEXT" or "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" or "CLOB" => ParseStringType(sqliteType),
            "JSON" => "JSON",
            "GUID" or "UUID" => "GUID",
            _ => "TEXT"
        };
    }

    private static string ParseDecimalType(string type)
    {
        var match = System.Text.RegularExpressions.Regex.Match(type, @"\((\s*\d+\s*)(?:,\s*(\d+)\s*)?\)");
        if (!match.Success)
            return "DECIMAL";
        var precision = match.Groups[1].Value.Trim();
        var scale = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
        return scale != null ? $"DECIMAL({precision},{scale})" : $"DECIMAL({precision})";
    }

    private static string ParseStringType(string type)
    {
        var match = System.Text.RegularExpressions.Regex.Match(type, @"\(\s*(\d+)\s*\)");
        if (match.Success)
            return $"VARCHAR({match.Groups[1].Value.Trim()})";
        return "TEXT";
    }

    private static string FormatDefaultValue(object value)
    {
        if (value is null || value == DBNull.Value)
            return "NULL";
        if (value is string s)
            return $"'{s.Replace("'", "''")}'";
        if (value is bool b)
            return b ? "TRUE" : "FALSE";
        if (value is DateTime dt)
            return $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'";
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL";
    }

    // ── Data copy ──

    private static CopyRowsResult CopyRows(SqliteConnection sourceConnection, WalhallaSqlDbConnection targetConnection, TableSchema schema, int batchSize, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var orderedColumns = schema.Columns.OrderBy(c => c.Ordinal).ToList();
        var sourceSql = $"SELECT {string.Join(", ", orderedColumns.Select(c => QuoteIdentifier(c.Name)))} FROM {QuoteIdentifier(schema.TableName)}";
        var insertSql = BuildInsertSql(schema);

        using var select = sourceConnection.CreateCommand();
        select.CommandText = sourceSql;

        using var reader = select.ExecuteReader();
        using var transaction = targetConnection.BeginTransaction();
        using var insert = targetConnection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = insertSql;

        var targetParameters = orderedColumns
            .Select((_, i) =>
            {
                var p = insert.CreateParameter();
                p.ParameterName = $"@p{i}";
                insert.Parameters.Add(p);
                return p;
            })
            .ToArray();

        long imported = 0;
        long failed = 0;
        long rowNum = 0;
        string? firstError = null;

        while (reader.Read())
        {
            rowNum++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                for (var i = 0; i < orderedColumns.Count; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    targetParameters[i].Value = ConvertSqliteValue(value) ?? DBNull.Value;
                }

                insert.ExecuteNonQuery();
                imported++;

                if (batchSize > 0 && imported % batchSize == 0)
                    progress?.Report($"[{schema.TableName}] imported {imported} rows...");
            }
            catch (Exception ex)
            {
                failed++;
                var rowError = $"[{schema.TableName}] skipped source row {rowNum}: {DescribeException(ex)}";
                firstError ??= rowError;
                progress?.Report(rowError);
            }
        }

        transaction.Commit();

        // Recreate indexes after bulk load.
        foreach (var idx in schema.Indexes)
        {
            try
            {
                var idxSql = BuildCreateIndexSql(schema.TableName, idx);
                ExecuteNonQuery(targetConnection, idxSql);
            }
            catch (Exception ex)
            {
                progress?.Report($"[{schema.TableName}] index '{idx.Name}' recreation failed: {DescribeException(ex)}");
            }
        }

        return new CopyRowsResult(imported, failed, firstError);
    }

    private static string BuildInsertSql(TableSchema schema)
    {
        var orderedColumns = schema.Columns.OrderBy(c => c.Ordinal).ToList();
        var names = string.Join(", ", orderedColumns.Select(c => QuoteIdentifier(c.Name)));
        var values = string.Join(", ", orderedColumns.Select((_, i) => $"@p{i}"));
        return $"INSERT INTO {QuoteIdentifier(schema.TableName)} ({names}) VALUES ({values})";
    }

    private static string BuildCreateIndexSql(string tableName, IndexInfo index)
    {
        var unique = index.IsUnique ? "UNIQUE " : "";
        var columns = string.Join(", ", index.Columns.Select(QuoteIdentifier));
        return $"CREATE {unique}INDEX {QuoteIdentifier(index.Name)} ON {QuoteIdentifier(tableName)} ({columns})";
    }

    private static object? ConvertSqliteValue(object? value)
    {
        if (value is null || value == DBNull.Value)
            return null;
        if (value is long l && l >= int.MinValue && l <= int.MaxValue)
            return (int)l; // WalhallaSql INT expects int, not long.
        if (value is byte[] bytes)
            return bytes;
        return value;
    }

    private static long CountSourceRows(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)}";
        var result = cmd.ExecuteScalar();
        return result is long l ? l : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static long CountTargetRows(WalhallaSqlDbConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)}";
        var result = cmd.ExecuteScalar();
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static void ExecuteNonQuery(WalhallaSqlDbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryDropTable(WalhallaSqlDbConnection connection, string tableName)
    {
        try
        {
            ExecuteNonQuery(connection, $"DROP TABLE {QuoteIdentifier(tableName)}");
        }
        catch { }
    }

    private static void ValidateRequest(SqliteMigrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
            throw new ArgumentException("SQLite source path is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetPath))
            throw new ArgumentException("Walhalla target path is required.", nameof(request));
    }

    private static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Identifier must not be empty.", nameof(name));
        return name;
    }

    private static string DescribeException(Exception exception)
    {
        if (!string.IsNullOrWhiteSpace(exception.Message))
            return $"{exception.GetType().Name}: {exception.Message}";
        return exception.GetType().Name;
    }

    // ── Records ──

    private sealed record TableSchema(string TableName, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<IndexInfo> Indexes);
    private sealed record ColumnInfo(int Ordinal, string Name, string Type, bool NotNull, object? DefaultValue, bool IsPrimaryKey);
    private sealed record IndexInfo(string Name, IReadOnlyList<string> Columns, bool IsUnique);
    private sealed record CopyRowsResult(long ImportedRows, long FailedRows, string? FirstError);
}

public sealed record SqliteMigrationRequest
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public string TargetDatabase { get; init; } = "App";
    public int BatchSize { get; init; } = 5000;
    public bool DropAndRecreate { get; init; } = false;
    public IReadOnlyList<string>? Tables { get; init; }
}

public sealed record SqliteMigrationTableResult(
    string TableName,
    long ImportedRows,
    long SourceRows,
    long TargetRows,
    TimeSpan Duration,
    bool Success,
    string? Error);

public sealed record SqliteMigrationResult(
    IReadOnlyList<SqliteMigrationTableResult> Tables,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc)
{
    public bool Success => Tables.Count > 0 && Tables.All(t => t.Success);
    public long ImportedRows => Tables.Sum(t => t.ImportedRows);
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
}
