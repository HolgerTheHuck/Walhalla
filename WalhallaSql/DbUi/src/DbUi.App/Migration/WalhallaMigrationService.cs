using System.Data;
using System.Globalization;
using System.IO;
using Microsoft.Data.SqlClient;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.AdoNet.SqlClient;
using WalhallaSql.Core;

namespace DbUi.App.Migration;

public sealed class WalhallaMigrationService
{
    public Task<IReadOnlyList<MigrationSourceTableInfo>> LoadTablesAsync(
        MigrationSourceRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => LoadTables(request, cancellationToken), cancellationToken);

    public Task<MigrationResult> MigrateAsync(
        MigrationRequest request, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Migrate(request, progress, cancellationToken), cancellationToken);

    public IReadOnlyList<MigrationSourceTableInfo> LoadTables(
        MigrationSourceRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSourceRequest(request);

        using var sourceConnection = new SqlConnection(request.ConnectionString);
        sourceConnection.Open();

        var tableNames = LoadTableNames(sourceConnection, request.Schema);
        var tables = new List<MigrationSourceTableInfo>(tableNames.Count);

        foreach (var tableName in tableNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = LoadTableSchema(sourceConnection, request.Schema, tableName);
            var rowCount = request.IncludeRowCounts
                ? CountSourceRows(sourceConnection, schema)
                : null;
            tables.Add(new MigrationSourceTableInfo(
                schema.SchemaName, schema.TableName,
                schema.Columns.OrderBy(c => c.Ordinal).ToArray(), rowCount));
        }

        return tables;
    }

    public MigrationResult Migrate(
        MigrationRequest request, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateMigrationRequest(request);

        var startedAtUtc = DateTime.UtcNow;
        var normalizedTables = request.Tables
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        WalhallaEngine engine;
        if (request.IsInMemory)
        {
            engine = WalhallaEngine.InMemory();
        }
        else
        {
            Directory.CreateDirectory(request.TargetPath);
            var options = new WalhallaOptions(request.TargetPath)
            {
                WalSyncMode = WalSyncMode.None,
                OdsPageSizeBytes = 65536,
            };
            engine = new WalhallaEngine(options);
        }

        using var engineDispose = engine;
        using var sourceConnection = new SqlConnection(request.ConnectionString);
        sourceConnection.Open();

        using var targetConnection = new WalhallaSqlDbConnection(engine);
        targetConnection.Open();

        progress?.Report($"Source: MSSQL ({sourceConnection.DataSource})");
        progress?.Report($"Target: WalhallaSql ({(request.IsInMemory ? "In-Memory" : request.TargetPath)})");
        progress?.Report($"Tables: {string.Join(", ", normalizedTables)}");
        progress?.Report(string.Empty);

        var tableResults = new List<MigrationTableResult>(normalizedTables.Length);

        foreach (var table in normalizedTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tableStartedAtUtc = DateTime.UtcNow;

            try
            {
                progress?.Report($"[{table}] loading schema...");
                var schema = LoadTableSchema(sourceConnection, request.Schema, table);
                if (schema.Columns.Count == 0)
                    throw new InvalidOperationException(
                        $"Table '{request.Schema}.{table}' has no readable columns.");

                if (request.DropAndRecreate)
                    TryDropTable(targetConnection, table);

                var createSql = BuildCreateTableSql(schema);
                try
                {
                    ExecuteNonQuery(targetConnection, createSql);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to create target table '{schema.TableName}'. SQL: {createSql}", ex);
                }

                var copyResult = CopyRows(
                    sourceConnection, targetConnection, schema,
                    request.BatchSize, progress, cancellationToken);
                engine.Checkpoint();
                var importedRows = copyResult.ImportedRows;
                var sourceCount = CountSourceRows(sourceConnection, schema) ?? 0;
                var targetCount = CountTargetRows(targetConnection, schema.TableName);
                var duration = DateTime.UtcNow - tableStartedAtUtc;
                var errors = new List<string>();
                if (copyResult.FailedRows > 0)
                    errors.Add(
                        $"Skipped {copyResult.FailedRows} rows. First error: {copyResult.FirstError}");

                if (sourceCount != targetCount || importedRows != targetCount)
                    errors.Add(
                        $"Row count mismatch for table '{table}'. source={sourceCount}, target={targetCount}, imported={importedRows}");

                var success = errors.Count == 0;
                var error = success ? null : string.Join(" | ", errors);
                var status = success ? "OK"
                    : copyResult.FailedRows > 0 ? "WARN"
                    : "MISMATCH";
                progress?.Report(
                    $"[{table}] done in {duration.TotalSeconds:F1}s | imported={importedRows} | skipped={copyResult.FailedRows} | source={sourceCount} | target={targetCount} | {status}");
                progress?.Report(string.Empty);

                tableResults.Add(new MigrationTableResult(
                    table, importedRows, sourceCount, targetCount, duration, success, error));
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - tableStartedAtUtc;
                var error = DescribeException(ex);
                progress?.Report($"[{table}] failed after {duration.TotalSeconds:F1}s | {error}");
                progress?.Report(string.Empty);
                tableResults.Add(new MigrationTableResult(
                    table, 0, 0, 0, duration, false, error));
            }
        }

        return new MigrationResult(tableResults, startedAtUtc, DateTime.UtcNow);
    }

    private static void ValidateSourceRequest(MigrationSourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            throw new ArgumentException("MSSQL connection string is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Schema))
            throw new ArgumentException("Source schema is required.", nameof(request));
    }

    private static void ValidateMigrationRequest(MigrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            throw new ArgumentException("MSSQL connection string is required.", nameof(request));
        if (!request.IsInMemory && string.IsNullOrWhiteSpace(request.TargetPath))
            throw new ArgumentException("Target path is required for file-based databases.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Schema))
            throw new ArgumentException("Source schema is required.", nameof(request));
        if (request.Tables.Count == 0)
            throw new ArgumentException("At least one table must be selected.", nameof(request));
    }

    private static IReadOnlyList<string> LoadTableNames(
        SqlConnection sourceConnection, string schemaName)
    {
        const string sql = """
SELECT t.TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES t
WHERE t.TABLE_SCHEMA = @schema
  AND t.TABLE_TYPE = 'BASE TABLE'
ORDER BY t.TABLE_NAME;
""";
        using var command = sourceConnection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@schema", schemaName);

        var tableNames = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            tableNames.Add(reader.GetString(0));

        return tableNames;
    }

    private static IReadOnlyList<MigrationSourceColumnInfo> LoadColumns(
        SqlConnection sourceConnection, string schemaName, string tableName)
    {
        const string sql = """
SELECT
    c.ORDINAL_POSITION,
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.NUMERIC_PRECISION,
    c.NUMERIC_SCALE,
    c.IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA = @schema
  AND c.TABLE_NAME = @table
ORDER BY c.ORDINAL_POSITION;
""";
        using var command = sourceConnection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@schema", schemaName);
        command.Parameters.AddWithValue("@table", tableName);

        var columns = new List<MigrationSourceColumnInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(new MigrationSourceColumnInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetByte(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                string.Equals(reader.GetString(6), "YES", StringComparison.OrdinalIgnoreCase),
                false));
        }

        return columns;
    }

    private static HashSet<string> LoadPrimaryKeyColumns(
        SqlConnection sourceConnection, string schemaName, string tableName)
    {
        const string sql = """
SELECT ku.COLUMN_NAME
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
  ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
 AND tc.TABLE_SCHEMA = ku.TABLE_SCHEMA
WHERE tc.TABLE_SCHEMA = @schema
  AND tc.TABLE_NAME = @table
  AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY';
""";
        using var command = sourceConnection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@schema", schemaName);
        command.Parameters.AddWithValue("@table", tableName);

        var primaryKeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            primaryKeyColumns.Add(reader.GetString(0));

        return primaryKeyColumns;
    }

    private static TableSchema LoadTableSchema(
        SqlConnection sourceConnection, string schemaName, string tableName)
    {
        var columns = LoadColumns(sourceConnection, schemaName, tableName).ToList();
        var primaryKeyColumns = LoadPrimaryKeyColumns(sourceConnection, schemaName, tableName);

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            columns[index] = column with { IsPrimaryKey = primaryKeyColumns.Contains(column.Name) };
        }

        return new TableSchema(schemaName, tableName, columns);
    }

    private static string BuildCreateTableSql(TableSchema schema)
    {
        var primaryKeyColumnCount = schema.Columns.Count(c => c.IsPrimaryKey);
        var columnSql = schema.Columns
            .OrderBy(c => c.Ordinal)
            .Select(c => BuildColumnDefinition(c, primaryKeyColumnCount == 1))
            .ToList();

        var primaryKeyColumns = schema.Columns
            .Where(c => c.IsPrimaryKey)
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Name)
            .ToList();

        if (primaryKeyColumns.Count > 1)
            columnSql.Add($"PRIMARY KEY ({string.Join(", ", primaryKeyColumns)})");

        return $"CREATE TABLE {schema.TableName} ({string.Join(", ", columnSql)})";
    }

    private static string BuildColumnDefinition(
        MigrationSourceColumnInfo column, bool emitInlinePrimaryKey)
    {
        var targetType = MapSourceTypeToWalhalla(column);
        var nullableSql = column.IsNullable && !column.IsPrimaryKey ? "" : " NOT NULL";
        var primaryKeySql = column.IsPrimaryKey && emitInlinePrimaryKey ? " PRIMARY KEY" : "";
        if (column.IsPrimaryKey && emitInlinePrimaryKey)
            nullableSql = "";

        return $"{column.Name} {targetType}{nullableSql}{primaryKeySql}";
    }

    private static string MapSourceTypeToWalhalla(MigrationSourceColumnInfo column)
    {
        var source = column.SourceType.ToLowerInvariant();

        return source switch
        {
            "tinyint" or "smallint" => "SMALLINT",
            "int" => "INT",
            "bigint" => "BIGINT",
            "bit" => "BOOLEAN",
            "float" or "real" => "DOUBLE",
            "decimal" or "numeric" => ResolveDecimalType(column),
            "money" => ResolveDecimalType(column, 19, 4),
            "smallmoney" => ResolveDecimalType(column, 10, 4),
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "DATETIME",
            "time" => "TIME",
            "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => "VARBINARY",
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml" => "STRING",
            "uniqueidentifier" => "GUID",
            _ => "STRING"
        };
    }

    private static string ResolveDecimalType(
        MigrationSourceColumnInfo column, int? fallbackPrecision = null, int? fallbackScale = null)
    {
        var precision = column.Precision ?? fallbackPrecision;
        var scale = column.Scale ?? fallbackScale;

        if (precision is null || precision <= 0)
            return "DECIMAL";

        if (scale is null || scale < 0)
            return $"DECIMAL({precision.Value})";

        var normalizedScale = Math.Min(scale.Value, precision.Value);
        return $"DECIMAL({precision.Value},{normalizedScale})";
    }

    private static CopyRowsResult CopyRows(
        SqlConnection sourceConnection,
        WalhallaSqlDbConnection targetConnection,
        TableSchema schema,
        int batchSize,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var orderedColumns = schema.Columns.OrderBy(c => c.Ordinal).ToList();
        var sourceSql =
            $"SELECT {string.Join(", ", orderedColumns.Select(c => $"[{c.Name.Replace("]", "]]", StringComparison.Ordinal)}]"))} FROM [{schema.SchemaName.Replace("]", "]]", StringComparison.Ordinal)}].[{schema.TableName.Replace("]", "]]", StringComparison.Ordinal)}]";

        using var select = sourceConnection.CreateCommand();
        select.CommandText = sourceSql;

        using var reader = select.ExecuteReader(CommandBehavior.SequentialAccess);

        long imported = 0;
        long failed = 0;
        long sourceRowNumber = 0;
        string? firstError = null;
        var rowBuffer = new List<object?[]>(Math.Max(batchSize, 1));

        while (reader.Read())
        {
            sourceRowNumber++;
            cancellationToken.ThrowIfCancellationRequested();

            var rowValues = new object?[orderedColumns.Count];
            bool rowOk = true;
            for (var index = 0; index < orderedColumns.Count; index++)
            {
                var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
                try
                {
                    rowValues[index] = ConvertValue(value) ?? DBNull.Value;
                }
                catch (Exception ex)
                {
                    rowOk = false;
                    failed++;
                    var rowError =
                        $"[{schema.TableName}] skipped source row {sourceRowNumber}: Column '{orderedColumns[index].Name}' could not be converted. Value={FormatValueForError(value)}. {DescribeException(ex)}";
                    firstError ??= rowError;
                    progress?.Report(rowError);
                    break;
                }
            }

            if (!rowOk)
                continue;

            rowBuffer.Add(rowValues);

            if (rowBuffer.Count >= batchSize)
            {
                ExecuteBatchInsert(targetConnection, schema.TableName, orderedColumns, rowBuffer, progress, ref imported, ref failed, ref firstError, sourceRowNumber, schema.TableName);
                rowBuffer.Clear();
            }
        }

        if (rowBuffer.Count > 0)
        {
            ExecuteBatchInsert(targetConnection, schema.TableName, orderedColumns, rowBuffer, progress, ref imported, ref failed, ref firstError, sourceRowNumber, schema.TableName);
        }

        return new CopyRowsResult(imported, failed, firstError);
    }

    private static void ExecuteBatchInsert(
        WalhallaSqlDbConnection targetConnection,
        string tableName,
        IReadOnlyList<MigrationSourceColumnInfo> orderedColumns,
        List<object?[]> rows,
        IProgress<string>? progress,
        ref long imported,
        ref long failed,
        ref string? firstError,
        long sourceRowNumber,
        string tableNameForLog)
    {
        using var transaction = targetConnection.BeginTransaction();
        try
        {
            var sql = BuildMultiRowInsertSql(tableName, orderedColumns, rows);
            using var insert = targetConnection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = sql;
            insert.ExecuteNonQuery();
            transaction.Commit();
            imported += rows.Count;
            progress?.Report($"[{tableNameForLog}] imported {imported} rows...");
        }
        catch (Exception batchEx)
        {
            transaction.Rollback();
            // Fallback: row-by-row to identify the failing row(s)
            foreach (var row in rows)
            {
                using var tx2 = targetConnection.BeginTransaction();
                try
                {
                    var sql = BuildSingleRowInsertSql(tableName, orderedColumns, row);
                    using var insert = targetConnection.CreateCommand();
                    insert.Transaction = tx2;
                    insert.CommandText = sql;
                    insert.ExecuteNonQuery();
                    tx2.Commit();
                    imported++;
                }
                catch (Exception ex)
                {
                    tx2.Rollback();
                    failed++;
                    var rowError = $"[{tableNameForLog}] skipped source row (batch fallback): {DescribeException(ex)}";
                    firstError ??= rowError;
                    progress?.Report(rowError);
                }
            }
        }
    }

    private static string BuildMultiRowInsertSql(string tableName, IReadOnlyList<MigrationSourceColumnInfo> orderedColumns, List<object?[]> rows)
    {
        var names = string.Join(", ", orderedColumns.Select(c => c.Name));
        var valueGroups = new System.Text.StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) valueGroups.Append(", ");
            valueGroups.Append('(');
            var row = rows[r];
            for (int c = 0; c < orderedColumns.Count; c++)
            {
                if (c > 0) valueGroups.Append(", ");
                valueGroups.Append(ToSqlLiteral(row[c]));
            }
            valueGroups.Append(')');
        }
        return $"INSERT INTO {tableName} ({names}) VALUES {valueGroups}";
    }

    private static string BuildSingleRowInsertSql(string tableName, IReadOnlyList<MigrationSourceColumnInfo> orderedColumns, object?[] row)
    {
        var names = string.Join(", ", orderedColumns.Select(c => c.Name));
        var values = string.Join(", ", row.Select(ToSqlLiteral));
        return $"INSERT INTO {tableName} ({names}) VALUES ({values})";
    }

    private static string ToSqlLiteral(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "NULL";

        // Use the engine's literal formatter for consistency
        return WalhallaSql.AdoNet.SqlClient.SqlLiteralFormatter.ToLiteral(value);
    }

    private static object? ConvertValue(object? value)
    {
        if (value is null || value == DBNull.Value)
            return null;

        return value switch
        {
            decimal number => number,
            DateTimeOffset dto => dto.UtcDateTime,
            Guid guid => guid,
            TimeSpan timeSpan => DateTime.UnixEpoch.Add(timeSpan),
            _ => ConvertNonStandard(value)
        };
    }

    private static object? ConvertNonStandard(object value)
    {
        var typeName = value.GetType().FullName ?? "";
        // SqlGuid from Microsoft.Data.SqlClient / System.Data.SqlClient — does not implement IConvertible.
        if (typeName == "Microsoft.Data.SqlTypes.SqlGuid" || typeName == "System.Data.SqlTypes.SqlGuid")
            return ((dynamic)value).Value;

        return value;
    }

    private static string BuildInsertSql(TableSchema schema)
    {
        var orderedColumns = schema.Columns.OrderBy(c => c.Ordinal).ToList();
        var names = string.Join(", ", orderedColumns.Select(c => c.Name));
        var values = string.Join(", ", orderedColumns.Select((_, index) => $"@p{index}"));
        return $"INSERT INTO {schema.TableName} ({names}) VALUES ({values})";
    }

    private static long? CountSourceRows(SqlConnection sourceConnection, TableSchema schema)
    {
        using var command = sourceConnection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT_BIG(*) FROM [{schema.SchemaName.Replace("]", "]]", StringComparison.Ordinal)}].[{schema.TableName.Replace("]", "]]", StringComparison.Ordinal)}]";
        var count = command.ExecuteScalar();
        return count is null || count == DBNull.Value
            ? 0
            : Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    private static long CountTargetRows(
        WalhallaSqlDbConnection targetConnection, string tableName)
    {
        using var command = targetConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        var count = command.ExecuteScalar();
        return count is null || count == DBNull.Value
            ? 0
            : Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    private static void ExecuteNonQuery(WalhallaSqlDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryDropTable(WalhallaSqlDbConnection connection, string tableName)
    {
        try
        {
            ExecuteNonQuery(connection, $"DROP TABLE {tableName}");
        }
        catch
        {
        }
    }

    private static string DescribeException(Exception exception)
    {
        if (!string.IsNullOrWhiteSpace(exception.Message))
            return $"{exception.GetType().Name}: {exception.Message}";
        return exception.GetType().Name;
    }

    private static string FormatValueForError(object? value)
    {
        if (value is null || value == DBNull.Value)
            return "<null>";
        if (value is string text)
            return $"'{(text.Length <= 120 ? text : text[..117] + "...")}'";
        if (value is byte[] buffer)
            return $"<binary:{buffer.Length} bytes>";
        return Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? value.ToString() ?? "<unprintable>";
    }

    private sealed record TableSchema(
        string SchemaName, string TableName,
        IReadOnlyList<MigrationSourceColumnInfo> Columns);

    private sealed record CopyRowsResult(
        long ImportedRows, long FailedRows, string? FirstError);
}

public sealed record MigrationSourceRequest(
    string ConnectionString, string Schema, bool IncludeRowCounts = false);

public sealed record MigrationRequest(
    string ConnectionString,
    string TargetPath,
    bool IsInMemory,
    string Schema,
    IReadOnlyList<string> Tables,
    int BatchSize,
    bool DropAndRecreate);

public sealed record MigrationSourceTableInfo(
    string SchemaName, string TableName,
    IReadOnlyList<MigrationSourceColumnInfo> Columns, long? RowCount)
{
    public string QualifiedName => $"{SchemaName}.{TableName}";
    public int ColumnCount => Columns.Count;
    public int PrimaryKeyCount => Columns.Count(c => c.IsPrimaryKey);
}

public sealed record MigrationSourceColumnInfo(
    int Ordinal,
    string Name,
    string SourceType,
    int? MaxLength,
    int? Precision,
    int? Scale,
    bool IsNullable,
    bool IsPrimaryKey);

public sealed record MigrationTableResult(
    string TableName,
    long ImportedRows,
    long SourceRows,
    long TargetRows,
    TimeSpan Duration,
    bool Success,
    string? Error);

public sealed record MigrationResult(
    IReadOnlyList<MigrationTableResult> Tables,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc)
{
    public bool Success => Tables.Count > 0 && Tables.All(t => t.Success);
    public long ImportedRows => Tables.Sum(t => t.ImportedRows);
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
}
