using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using WalhallaSql.AdoNet;

namespace WalhallaSql.Cli;

public sealed class MssqlMigrationService
{
    public Task<IReadOnlyList<MssqlSourceTableInfo>> LoadTablesAsync(MssqlSchemaDiscoveryRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => LoadTables(request, cancellationToken), cancellationToken);

    public Task<MssqlMigrationResult> MigrateAsync(MssqlMigrationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Migrate(request, progress, cancellationToken), cancellationToken);

    public IReadOnlyList<MssqlSourceTableInfo> LoadTables(MssqlSchemaDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        ValidateDiscoveryRequest(request);

        using var sourceConnection = new SqlConnection(request.MssqlConnectionString);
        sourceConnection.Open();

        var tableNames = LoadTableNames(sourceConnection, request.SourceSchema);
        var tables = new List<MssqlSourceTableInfo>(tableNames.Count);

        foreach (var tableName in tableNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var schema = LoadTableSchema(sourceConnection, request.SourceSchema, tableName);
            var rowCount = request.IncludeRowCounts ? CountSourceRows(sourceConnection, schema) : null;
            tables.Add(new MssqlSourceTableInfo(schema.SchemaName, schema.TableName, schema.Columns.OrderBy(c => c.Ordinal).ToArray(), rowCount));
        }

        return tables;
    }

    public MssqlMigrationResult Migrate(MssqlMigrationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateMigrationRequest(request);

        var startedAtUtc = DateTime.UtcNow;
        var normalizedTables = request.Tables
            .Where(static table => !string.IsNullOrWhiteSpace(table))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Directory.CreateDirectory(request.WalhallaPath);

        using var engine = WalhallaEngine.Open(request.WalhallaPath);

        using var sourceConnection = new SqlConnection(request.MssqlConnectionString);
        sourceConnection.Open();

        using var targetConnection = new WalhallaSqlDbConnection(engine, $"DataSource=embedded;Database={request.WalhallaDatabase}");
        targetConnection.Open();

        progress?.Report($"Source: MSSQL ({sourceConnection.DataSource})");
        progress?.Report($"Target: Walhalla ({request.WalhallaPath})");
        progress?.Report($"Tables: {string.Join(", ", normalizedTables)}");
        progress?.Report(string.Empty);

        var tableResults = new List<MssqlMigrationTableResult>(normalizedTables.Length);

        foreach (var table in normalizedTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tableStartedAtUtc = DateTime.UtcNow;

            try
            {
                progress?.Report($"[{table}] loading schema...");
                var schema = LoadTableSchema(sourceConnection, request.SourceSchema, table);
                if (schema.Columns.Count == 0)
                    throw new InvalidOperationException($"Table '{request.SourceSchema}.{table}' has no readable columns.");

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
                var sourceCount = CountSourceRows(sourceConnection, schema) ?? 0;
                var targetCount = CountTargetRows(targetConnection, schema.TableName);
                var duration = DateTime.UtcNow - tableStartedAtUtc;
                var errors = new List<string>();
                if (copyResult.FailedRows > 0)
                    errors.Add($"Skipped {copyResult.FailedRows} rows. First error: {copyResult.FirstError}");

                if (sourceCount != targetCount || importedRows != targetCount)
                    errors.Add($"Row count mismatch for table '{table}'. source={sourceCount}, target={targetCount}, imported={importedRows}");

                var success = errors.Count == 0;
                var error = success ? null : string.Join(" | ", errors);
                var status = success
                    ? "OK"
                    : copyResult.FailedRows > 0
                        ? "WARN"
                        : "MISMATCH";
                progress?.Report($"[{table}] done in {duration.TotalSeconds:F1}s | imported={importedRows} | skipped={copyResult.FailedRows} | source={sourceCount} | target={targetCount} | {status}");
                progress?.Report(string.Empty);

                tableResults.Add(new MssqlMigrationTableResult(table, importedRows, sourceCount, targetCount, duration, success, error));
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - tableStartedAtUtc;
                var error = DescribeException(ex);
                progress?.Report($"[{table}] failed after {duration.TotalSeconds:F1}s | {error}");
                progress?.Report(string.Empty);
                tableResults.Add(new MssqlMigrationTableResult(table, 0, 0, 0, duration, false, error));
            }
        }

        return new MssqlMigrationResult(tableResults, startedAtUtc, DateTime.UtcNow);
    }

    private static void ValidateDiscoveryRequest(MssqlSchemaDiscoveryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MssqlConnectionString))
            throw new ArgumentException("MSSQL connection string is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.SourceSchema))
            throw new ArgumentException("Source schema is required.", nameof(request));
    }

    private static void ValidateMigrationRequest(MssqlMigrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MssqlConnectionString))
            throw new ArgumentException("MSSQL connection string is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.WalhallaPath))
            throw new ArgumentException("Walhalla path is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.WalhallaDatabase))
            throw new ArgumentException("Walhalla database name is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.SourceSchema))
            throw new ArgumentException("Source schema is required.", nameof(request));

        if (request.Tables.Count == 0)
            throw new ArgumentException("At least one table must be selected.", nameof(request));
    }

    private static IReadOnlyList<string> LoadTableNames(SqlConnection sourceConnection, string schemaName)
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

    private static IReadOnlyList<MssqlSourceColumnInfo> LoadColumns(SqlConnection sourceConnection, string schemaName, string tableName)
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

        var columns = new List<MssqlSourceColumnInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(new MssqlSourceColumnInfo(
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

    private static HashSet<string> LoadPrimaryKeyColumns(SqlConnection sourceConnection, string schemaName, string tableName)
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

    private static TableSchema LoadTableSchema(SqlConnection sourceConnection, string schemaName, string tableName)
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
        var primaryKeyColumnCount = schema.Columns.Count(column => column.IsPrimaryKey);
        var columnSql = schema.Columns
            .OrderBy(column => column.Ordinal)
            .Select(column => BuildColumnDefinition(column, primaryKeyColumnCount == 1))
            .ToList();

        var primaryKeyColumns = schema.Columns
            .Where(column => column.IsPrimaryKey)
            .OrderBy(column => column.Ordinal)
            .Select(column => QuoteIdentifier(column.Name))
            .ToList();

        if (primaryKeyColumns.Count > 1)
            columnSql.Add($"PRIMARY KEY ({string.Join(", ", primaryKeyColumns)})");

        return $"CREATE TABLE {QuoteIdentifier(schema.TableName)} ({string.Join(", ", columnSql)})";
    }

    private static string BuildColumnDefinition(MssqlSourceColumnInfo column, bool emitInlinePrimaryKey)
    {
        var targetType = MapSourceTypeToLayeredSql(column);
        var nullableSql = column.IsNullable && !column.IsPrimaryKey ? string.Empty : " NOT NULL";
        var primaryKeySql = column.IsPrimaryKey && emitInlinePrimaryKey ? " PRIMARY KEY" : string.Empty;
        if (column.IsPrimaryKey && emitInlinePrimaryKey)
            nullableSql = string.Empty;

        return $"{QuoteIdentifier(column.Name)} {targetType}{nullableSql}{primaryKeySql}";
    }

    private static string MapSourceTypeToLayeredSql(MssqlSourceColumnInfo column)
    {
        var source = column.SourceType.ToLowerInvariant();

        return source switch
        {
            "tinyint" or "smallint" or "int" => "INT",
            "bigint" => "BIGINT",
            "bit" => "BIT",
            "float" or "real" => "DOUBLE",
            "decimal" or "numeric" => ResolveDecimalType(column),
            "money" => ResolveDecimalType(column, 19, 4),
            "smallmoney" => ResolveDecimalType(column, 10, 4),
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" => "DATETIME",
            "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => "VARBINARY",
            "char" or "nchar" => $"VARCHAR({ResolveLength(column.MaxLength, 200)})",
            "varchar" or "nvarchar" => ResolveVarchar(column.MaxLength),
            "text" or "ntext" or "xml" => "TEXT",
            "uniqueidentifier" => "GUID",
            _ => "TEXT"
        };
    }

    private static string ResolveDecimalType(MssqlSourceColumnInfo column, int? fallbackPrecision = null, int? fallbackScale = null)
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

    private static string ResolveVarchar(int? maxLength)
    {
        if (maxLength is null || maxLength <= 0 || maxLength > 4000)
            return "TEXT";

        return $"VARCHAR({maxLength.Value})";
    }

    private static int ResolveLength(int? maxLength, int fallback)
    {
        if (maxLength is null || maxLength <= 0)
            return fallback;

        return Math.Min(maxLength.Value, 4000);
    }

    private static CopyRowsResult CopyRows(SqlConnection sourceConnection, WalhallaSqlDbConnection targetConnection, TableSchema schema, int batchSize, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var orderedColumns = schema.Columns.OrderBy(column => column.Ordinal).ToList();
        var sourceSql = $"SELECT {string.Join(", ", orderedColumns.Select(column => QuoteSourceIdentifier(column.Name)))} FROM {QuoteSourceIdentifier(schema.SchemaName)}.{QuoteSourceIdentifier(schema.TableName)}";
        var insertSql = BuildInsertSql(schema);

        using var select = sourceConnection.CreateCommand();
        select.CommandText = sourceSql;

        using var reader = select.ExecuteReader(CommandBehavior.SequentialAccess);
        using var transaction = targetConnection.BeginTransaction();
        using var insert = targetConnection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = insertSql;

        var targetParameters = orderedColumns
            .Select((_, index) =>
            {
                var parameter = insert.CreateParameter();
                parameter.ParameterName = $"@p{index}";
                insert.Parameters.Add(parameter);
                return parameter;
            })
            .ToArray();

        long imported = 0;
        long failed = 0;
        long sourceRowNumber = 0;
        string? firstError = null;
        while (reader.Read())
        {
            sourceRowNumber++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                for (var index = 0; index < orderedColumns.Count; index++)
                {
                    var value = reader.IsDBNull(index) ? null : reader.GetValue(index);

                    try
                    {
                        targetParameters[index].Value = ConvertValue(value) ?? DBNull.Value;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Column '{orderedColumns[index].Name}' at row {sourceRowNumber} could not be converted. Value={FormatValueForError(value)}",
                            ex);
                    }
                }

                insert.ExecuteNonQuery();
                imported++;

                if (batchSize > 0 && imported % batchSize == 0)
                    progress?.Report($"[{schema.TableName}] imported {imported} rows...");
            }
            catch (Exception ex)
            {
                failed++;
                var rowError = $"[{schema.TableName}] skipped source row {sourceRowNumber}: {DescribeException(ex)}";
                firstError ??= rowError;
                progress?.Report(rowError);
            }
        }

        transaction.Commit();
        return new CopyRowsResult(imported, failed, firstError);
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
            _ => value
        };
    }

    private static string BuildInsertSql(TableSchema schema)
    {
        var orderedColumns = schema.Columns.OrderBy(column => column.Ordinal).ToList();
        var names = string.Join(", ", orderedColumns.Select(column => QuoteIdentifier(column.Name)));
        var values = string.Join(", ", orderedColumns.Select((_, index) => $"@p{index}"));
        return $"INSERT INTO {QuoteIdentifier(schema.TableName)} ({names}) VALUES ({values})";
    }

    private static long? CountSourceRows(SqlConnection sourceConnection, TableSchema schema)
    {
        using var command = sourceConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {QuoteSourceIdentifier(schema.SchemaName)}.{QuoteSourceIdentifier(schema.TableName)}";
        var count = command.ExecuteScalar();
        return count is null || count == DBNull.Value ? 0 : Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    private static long CountTargetRows(WalhallaSqlDbConnection targetConnection, string tableName)
    {
        using var command = targetConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)}";
        var count = command.ExecuteScalar();
        return count is null || count == DBNull.Value ? 0 : Convert.ToInt64(count, CultureInfo.InvariantCulture);
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
            ExecuteNonQuery(connection, $"DROP TABLE {QuoteIdentifier(tableName)}");
        }
        catch
        {
        }
    }

    private static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Identifier must not be empty.", nameof(name));

        return name;
    }

    private static string QuoteSourceIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Identifier must not be empty.", nameof(name));

        return $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
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

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? "<unprintable>";
    }

    private sealed record TableSchema(string SchemaName, string TableName, IReadOnlyList<MssqlSourceColumnInfo> Columns);

    private sealed record CopyRowsResult(long ImportedRows, long FailedRows, string? FirstError);
}

public sealed record MssqlSchemaDiscoveryRequest(string MssqlConnectionString, string SourceSchema, bool IncludeRowCounts = false);

public sealed record MssqlMigrationRequest(
    string MssqlConnectionString,
    string WalhallaPath,
    string WalhallaDatabase,
    string SourceSchema,
    IReadOnlyList<string> Tables,
    int BatchSize,
    bool DropAndRecreate);

public sealed record MssqlSourceTableInfo(string SchemaName, string TableName, IReadOnlyList<MssqlSourceColumnInfo> Columns, long? RowCount)
{
    public string QualifiedName => $"{SchemaName}.{TableName}";
    public int ColumnCount => Columns.Count;
    public int PrimaryKeyCount => Columns.Count(column => column.IsPrimaryKey);
};

public sealed record MssqlSourceColumnInfo(
    int Ordinal,
    string Name,
    string SourceType,
    int? MaxLength,
    int? Precision,
    int? Scale,
    bool IsNullable,
    bool IsPrimaryKey);

public sealed record MssqlMigrationTableResult(
    string TableName,
    long ImportedRows,
    long SourceRows,
    long TargetRows,
    TimeSpan Duration,
    bool Success,
    string? Error);

public sealed record MssqlMigrationResult(IReadOnlyList<MssqlMigrationTableResult> Tables, DateTime StartedAtUtc, DateTime FinishedAtUtc)
{
    public bool Success => Tables.Count > 0 && Tables.All(table => table.Success);
    public long ImportedRows => Tables.Sum(table => table.ImportedRows);
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
}
