using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using WalhallaSql.AdoNet;
using WalhallaSql.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace WalhallaSql.EfCore.Migrations;

public sealed record WalhallaSqlMigrationPlan(IReadOnlyList<MigrationOperation> Operations)
{
    public bool HasChanges => Operations.Count > 0;
}

public sealed record WalhallaSqlMigrationResult(string MigrationId, int AppliedOperations, DateTime AppliedAtUtc);

public sealed record WalhallaSqlMigrationHistoryEntry(string MigrationId, DateTime AppliedAtUtc, int OperationCount);

public abstract record MigrationOperation;

public sealed record CreateTableOperation(SqlTableDefinition Table) : MigrationOperation;

public sealed record DropTableOperation(string CollectionName) : MigrationOperation;

public sealed record RenameTableOperation(string CollectionName, string NewCollectionName) : MigrationOperation;

public sealed record AddColumnOperation(string CollectionName, SqlColumnDefinition Column, string? DefaultValueLiteral) : MigrationOperation;

public sealed record DropColumnOperation(string CollectionName, string ColumnName) : MigrationOperation;

public sealed record RenameColumnOperation(string CollectionName, string OldColumnName, string NewColumnName) : MigrationOperation;

public sealed record AlterColumnOperation(string CollectionName, SqlColumnDefinition Column) : MigrationOperation;

public sealed record CreateIndexOperation(string CollectionName, SqlIndexDefinition Index) : MigrationOperation;

public sealed record DropIndexOperation(string CollectionName, string IndexName) : MigrationOperation;

public sealed record AddForeignKeyOperation(string CollectionName, SqlForeignKeyDefinition ForeignKey) : MigrationOperation;

public sealed record DropForeignKeyOperation(string CollectionName, string ConstraintName) : MigrationOperation;

public sealed class WalhallaSqlMigrationService
{
    private const string HistoryCollectionName = "__ef_migrations_history";
    private const string MigrationLockCollectionName = "__ef_migrations_lock";
    private static readonly TimeSpan DefaultMigrationLockWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMigrationLockStaleThreshold = TimeSpan.FromMinutes(10);

    private static bool IsInternalTable(string collectionName)
        => string.Equals(collectionName, HistoryCollectionName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(collectionName, MigrationLockCollectionName, StringComparison.OrdinalIgnoreCase);

    private readonly DbContext _context;
    private readonly WalhallaSqlDbConnection _connection;
    private readonly string _connectionStringIdentity;
    private readonly WalhallaSqlEfCoreOptions _options;
    private readonly ILogger<WalhallaSqlMigrationService>? _logger;

    public WalhallaSqlMigrationService(
        DbContext context,
        WalhallaSqlDbConnection connection,
        WalhallaSqlEfCoreOptions options,
        ILogger<WalhallaSqlMigrationService>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _connectionStringIdentity = !string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? _options.ConnectionString
            : "embedded-local";
    }

    public WalhallaSqlMigrationPlan PlanModelChanges()
    {
        return PlanModelChanges(_context.Model);
    }

    private WalhallaSqlMigrationPlan PlanModelChanges(IModel model)
    {
        var modelSnapshot = BuildModelSnapshot(model).ToList();
        var existingTables = ReadExistingTablesForPlanning(modelSnapshot);
        return PlanModelChanges(modelSnapshot, existingTables, includeFullConstraintDiff: false);
    }

    public WalhallaSqlMigrationPlan PlanModelChanges(IModel fromModel, IModel toModel)
    {
        var fromSnapshot = BuildModelSnapshot(fromModel).ToList();
        var toSnapshot = BuildModelSnapshot(toModel).ToList();

        var existingTables = fromSnapshot.ToDictionary(
            t => t.CollectionName,
            t => new SqlTableDefinition(t.CollectionName, t.Columns, t.Indexes, t.ForeignKeys),
            StringComparer.OrdinalIgnoreCase);

        return PlanModelChanges(toSnapshot, existingTables, includeFullConstraintDiff: true);
    }

    private WalhallaSqlMigrationPlan PlanModelChanges(
        IReadOnlyList<ModelTableSnapshot> targetTables,
        Dictionary<string, SqlTableDefinition> existingTables,
        bool includeFullConstraintDiff)
    {
        var workingExistingTables = new Dictionary<string, SqlTableDefinition>(existingTables, StringComparer.OrdinalIgnoreCase);
        var deferredForeignKeyAdds = new List<AddForeignKeyOperation>();

        var operations = new List<MigrationOperation>();

        var modelByName = targetTables.ToDictionary(table => table.CollectionName, StringComparer.OrdinalIgnoreCase);
        var removedTables = workingExistingTables.Values
            .Where(existing => !modelByName.ContainsKey(existing.CollectionName)
                && !IsInternalTable(existing.CollectionName))
            .ToList();

        var addedTables = targetTables
            .Where(model => !workingExistingTables.ContainsKey(model.CollectionName))
            .ToList();

        foreach (var removed in removedTables.ToArray())
        {
            var compatibleCandidates = addedTables
                .Where(added => HaveCompatibleTableShapeForRename(removed, added))
                .ToList();

            if (compatibleCandidates.Count == 0)
                continue;

            if (compatibleCandidates.Count > 1)
            {
                var names = string.Join(", ", compatibleCandidates.Select(candidate => candidate.CollectionName));
                throw new InvalidOperationException(
                    $"Ambiguous table rename detected for '{removed.CollectionName}'. " +
                    $"Multiple compatible targets found: {names}. Use explicit SQL rename migration for this change.");
            }

            var replacement = compatibleCandidates[0];

            var reverseMatches = removedTables
                .Where(existing => HaveCompatibleTableShapeForRename(existing, replacement))
                .ToList();

            if (reverseMatches.Count > 1)
            {
                var names = string.Join(", ", reverseMatches.Select(candidate => candidate.CollectionName));
                throw new InvalidOperationException(
                    $"Ambiguous table rename target '{replacement.CollectionName}'. " +
                    $"Multiple removed tables are shape-compatible: {names}. Use explicit SQL rename migration for this change.");
            }

            operations.Add(new RenameTableOperation(removed.CollectionName, replacement.CollectionName));

            removedTables.Remove(removed);
            addedTables.Remove(replacement);
            workingExistingTables.Remove(removed.CollectionName);
            workingExistingTables[replacement.CollectionName] = removed with { CollectionName = replacement.CollectionName };
        }

        foreach (var table in targetTables)
        {
            if (!workingExistingTables.TryGetValue(table.CollectionName, out var current))
            {
                operations.Add(new CreateTableOperation(new SqlTableDefinition(
                    table.CollectionName,
                    table.Columns,
                    Array.Empty<SqlIndexDefinition>(),
                    Projections: table.Projections)));

                foreach (var index in GetAdditionalIndexesForNewTable(table))
                {
                    operations.Add(new CreateIndexOperation(table.CollectionName, index));
                }

                foreach (var foreignKey in table.ForeignKeys)
                    deferredForeignKeyAdds.Add(new AddForeignKeyOperation(table.CollectionName, foreignKey));

                continue;
            }

            if (includeFullConstraintDiff)
                AddForeignKeyDropOperations(operations, table, current);

            var currentColumns = current.Columns
                .ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);

            var modelColumns = table.Columns
                .ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);

            var removedColumns = current.Columns
                .Where(column => !modelColumns.ContainsKey(column.Name) && !column.IsPrimaryKey)
                .ToList();

            var addedColumns = table.Columns
                .Where(column => !currentColumns.ContainsKey(column.Name) && !column.IsPrimaryKey)
                .ToList();

            var renameMatches = new List<(SqlColumnDefinition OldColumn, SqlColumnDefinition NewColumn)>();
            foreach (var removed in removedColumns.ToArray())
            {
                var compatibleCandidates = addedColumns
                    .Where(candidate => AreColumnsRenameCompatible(removed, candidate))
                    .ToList();

                if (compatibleCandidates.Count == 0)
                    continue;

                if (compatibleCandidates.Count > 1)
                {
                    var names = string.Join(", ", compatibleCandidates.Select(candidate => candidate.Name));
                    throw new InvalidOperationException(
                        $"Ambiguous column rename detected for '{table.CollectionName}.{removed.Name}'. " +
                        $"Multiple compatible target columns found: {names}. Use explicit SQL rename migration for this change.");
                }

                var replacement = compatibleCandidates[0];

                var reverseMatches = removedColumns
                    .Where(existing => AreColumnsRenameCompatible(existing, replacement))
                    .ToList();

                if (reverseMatches.Count > 1)
                {
                    var names = string.Join(", ", reverseMatches.Select(candidate => candidate.Name));
                    throw new InvalidOperationException(
                        $"Ambiguous column rename target '{table.CollectionName}.{replacement.Name}'. " +
                        $"Multiple removed columns are compatible: {names}. Use explicit SQL rename migration for this change.");
                }

                renameMatches.Add((removed, replacement));
                removedColumns.Remove(removed);
                addedColumns.Remove(replacement);
            }

            foreach (var rename in renameMatches)
            {
                operations.Add(new RenameColumnOperation(
                    table.CollectionName,
                    rename.OldColumn.Name,
                    rename.NewColumn.Name));
            }

            foreach (var modelColumn in addedColumns)
            {
                operations.Add(new AddColumnOperation(
                    table.CollectionName,
                    modelColumn,
                    GetDefaultLiteralForAddedColumn(modelColumn)));
            }

            foreach (var modelColumn in table.Columns)
            {
                if (!currentColumns.TryGetValue(modelColumn.Name, out var existingColumn))
                    continue;

                if (existingColumn.Type == modelColumn.Type && existingColumn.IsNullable == modelColumn.IsNullable)
                    continue;

                if (_options.StrictMigrationGuardrails)
                    ValidateSupportedAlterColumnChange(table.CollectionName, existingColumn, modelColumn);

                operations.Add(new AlterColumnOperation(table.CollectionName, modelColumn));
            }

            foreach (var removedColumn in removedColumns)
            {
                operations.Add(new DropColumnOperation(
                    table.CollectionName,
                    removedColumn.Name));
            }

            if (includeFullConstraintDiff)
            {
                AddIndexDiffOperations(operations, table, current);
                AddForeignKeyAddOperations(operations, table, current);
            }
        }

        operations.AddRange(deferredForeignKeyAdds);

        foreach (var removedTable in removedTables)
            operations.Add(new DropTableOperation(removedTable.CollectionName));

        var plan = new WalhallaSqlMigrationPlan(operations);
        _logger?.LogInformation("Planned {OperationCount} model changes.", plan.Operations.Count);
        return plan;
    }

    public WalhallaSqlMigrationResult ApplyPlannedChanges(string migrationId)
    {
        if (string.IsNullOrWhiteSpace(migrationId))
            throw new ArgumentException("MigrationId must not be empty.", nameof(migrationId));

        _logger?.LogInformation("Applying planned changes for migration '{MigrationId}'.", migrationId);

        return ExecuteWithMigrationLock(() =>
        {
            var plan = PlanModelChanges();

            // When auto-applying planned changes we only execute schema mutations that
            // the current EF model "owns".  DropTableOperations for tables that are not
            // part of this context's model (i.e. tables created externally or owned by
            // another context) are intentionally skipped.  Callers that explicitly want
            // to drop such tables can use ApplyPlan() with a hand-crafted plan.
            var opsToApply = plan.Operations
                .Where(op => op is not DropTableOperation)
                .ToList();
            var executablePlan = new WalhallaSqlMigrationPlan(opsToApply);

            _logger?.LogInformation("Applying {OperationCount} of {TotalOperationCount} operations for migration '{MigrationId}'.", executablePlan.Operations.Count, plan.Operations.Count, migrationId);

            ApplyPlan(executablePlan);
            SaveHistory(migrationId, plan.Operations.Count);

            _logger?.LogInformation("Migration '{MigrationId}' applied successfully with {OperationCount} operations.", migrationId, plan.Operations.Count);
            return new WalhallaSqlMigrationResult(migrationId, plan.Operations.Count, DateTime.UtcNow);
        });
    }

    public WalhallaSqlMigrationResult ApplyPlan(string migrationId, WalhallaSqlMigrationPlan plan)
    {
        if (string.IsNullOrWhiteSpace(migrationId))
            throw new ArgumentException("MigrationId must not be empty.", nameof(migrationId));

        if (plan == null)
            throw new ArgumentNullException(nameof(plan));

        _logger?.LogInformation("Applying explicit plan for migration '{MigrationId}' with {OperationCount} operations.", migrationId, plan.Operations.Count);

        return ExecuteWithMigrationLock(() =>
        {
            ApplyPlan(plan);
            SaveHistory(migrationId, plan.Operations.Count);

            _logger?.LogInformation("Explicit plan for migration '{MigrationId}' applied successfully.", migrationId);
            return new WalhallaSqlMigrationResult(migrationId, plan.Operations.Count, DateTime.UtcNow);
        });
    }

    public WalhallaSqlDatabaseInfo GetDatabaseInfo()
    {
        return _connection.GetStorageInfo();
    }

    public IReadOnlyList<WalhallaSqlMigrationHistoryEntry> GetHistory()
    {
        return GetHistoryViaSql();
    }

    public WalhallaSqlMigrationPlan PlanDownMigrationToEmpty()
    {
        var existingTables = ReadExistingTablesForPlanning(Array.Empty<ModelTableSnapshot>());
        var operations = new List<MigrationOperation>();

        foreach (var table in existingTables.Values)
        {
            foreach (var fk in table.ForeignKeys)
                operations.Add(new DropForeignKeyOperation(table.CollectionName, fk.ConstraintName));
        }

        foreach (var table in existingTables.Values)
        {
            if (table.CollectionName.Equals(HistoryCollectionName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (table.CollectionName.Equals(MigrationLockCollectionName, StringComparison.OrdinalIgnoreCase))
                continue;
            operations.Add(new DropTableOperation(table.CollectionName));
        }

        return new WalhallaSqlMigrationPlan(operations);
    }

    public WalhallaSqlMigrationPlan PlanDownMigration(IModel targetModel)
    {
        return PlanModelChanges(targetModel);
    }

    public void ClearHistory()
    {
        ClearHistoryViaSql();
    }

    public void RemoveHistoryEntriesAfter(string migrationId)
    {
        var history = GetHistory();
        var toRemove = history
            .Where(entry => string.Compare(entry.MigrationId, migrationId, StringComparison.OrdinalIgnoreCase) > 0)
            .Select(entry => entry.MigrationId)
            .ToList();

        RemoveHistoryEntries(toRemove);
    }

    public void RemoveHistoryEntries(IReadOnlyList<string> migrationIds)
    {
        if (migrationIds.Count == 0)
            return;

        foreach (var id in migrationIds)
            ExecuteSql($"DELETE FROM {HistoryCollectionName} WHERE MigrationId = {SqlLiteral(id)}");
    }

    private void ClearHistoryViaSql()
    {
        try
        {
            ExecuteSql($"DROP TABLE {HistoryCollectionName}");
        }
        catch
        {
            // Best-effort
        }
    }

    private void ApplyPlan(WalhallaSqlMigrationPlan plan)
    {
        ApplyPlanViaSql(plan);
    }

    private void SaveHistory(string migrationId, int operationCount)
    {
        SaveHistoryViaSql(migrationId, operationCount);
    }

    private T ExecuteWithMigrationLock<T>(Func<T> action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        var ownerId = Guid.NewGuid().ToString("N");
        var mutexName = BuildMigrationMutexName();
        _logger?.LogDebug("Acquiring migration lock '{MutexName}' (owner: {OwnerId}).", mutexName, ownerId);
        using var mutex = new Mutex(false, mutexName);

        var hasMutex = false;
        try
        {
            try
            {
                hasMutex = mutex.WaitOne(GetMigrationLockWaitTimeout());
            }
            catch (AbandonedMutexException)
            {
                hasMutex = true;
                _logger?.LogWarning("Migration lock '{MutexName}' was abandoned; acquired by new owner {OwnerId}.", mutexName, ownerId);
            }

            if (!hasMutex)
            {
                throw new InvalidOperationException(
                    $"Could not acquire migration lock within {GetMigrationLockWaitTimeout().TotalSeconds:0.###} seconds.");
            }

            _logger?.LogDebug("Migration lock '{MutexName}' acquired by {OwnerId}.", mutexName, ownerId);

            ApplyMigrationLockTestHoldIfConfigured();

            try
            {
                return action();
            }
            finally
            {
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Migration failed under lock '{MutexName}' (owner: {OwnerId}).", mutexName, ownerId);
            throw;
        }
        finally
        {
            if (hasMutex)
            {
                mutex.ReleaseMutex();
                _logger?.LogDebug("Migration lock '{MutexName}' released by {OwnerId}.", mutexName, ownerId);
            }
        }
    }

    private static void ApplyMigrationLockTestHoldIfConfigured()
    {
        var holdMsText = Environment.GetEnvironmentVariable("LAYEREDSQL_EF_TEST_HOLD_MIGRATION_LOCK_MS");
        if (!int.TryParse(holdMsText, out var holdMs) || holdMs <= 0)
            return;

        Thread.Sleep(holdMs);
    }

    public static string BuildEmbeddedPathMutexName(string databasePath, string databaseName, string engineTypeName)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must not be empty.", nameof(databasePath));

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("Database name must not be empty.", nameof(databaseName));

        if (string.IsNullOrWhiteSpace(engineTypeName))
            throw new ArgumentException("Engine type name must not be empty.", nameof(engineTypeName));

        var normalizedPath = Path.GetFullPath(databasePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return BuildMutexNameFromIdentity($"{engineTypeName}|{normalizedPath.ToUpperInvariant()}|{databaseName}");
    }

    public static string BuildEmbeddedPathPreOpenMutexName(string databasePath, string databaseName, string engineTypeName)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must not be empty.", nameof(databasePath));

        var normalizedPath = Path.GetFullPath(databasePath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedIdentity = OperatingSystem.IsWindows()
            ? normalizedPath.ToUpperInvariant()
            : normalizedPath;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIdentity));
        var shortHash = Convert.ToHexString(hashBytes.AsSpan(0, 12));
        return $"Local\\WalhallaSqlEmbeddedOpen_{shortHash}";
    }

    private string BuildMigrationMutexName()
    {
        return BuildMutexNameFromIdentity(_connectionStringIdentity);
    }

    private static string BuildMutexNameFromIdentity(string identity)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var shortHash = Convert.ToHexString(hashBytes.AsSpan(0, 12));
        return $"Local\\WalhallaSqlEfCoreMigration_{shortHash}";
    }

    private TimeSpan GetMigrationLockWaitTimeout()
    {
        return _options.MigrationLockWaitTimeout > TimeSpan.Zero
            ? _options.MigrationLockWaitTimeout
            : DefaultMigrationLockWaitTimeout;
    }

    private TimeSpan GetMigrationLockStaleThreshold()
    {
        return _options.MigrationLockStaleThreshold > TimeSpan.Zero
            ? _options.MigrationLockStaleThreshold
            : DefaultMigrationLockStaleThreshold;
    }

    private Dictionary<string, SqlTableDefinition> ReadExistingTablesForPlanning(IReadOnlyList<ModelTableSnapshot> modelSnapshot)
    {
        return ReadExistingTablesForPlanningViaSql(modelSnapshot);
    }

    private Dictionary<string, SqlTableDefinition> ReadExistingTablesForPlanningViaSql(IReadOnlyList<ModelTableSnapshot> modelSnapshot)
    {
        Dictionary<string, SqlTableDefinition> existing;
        try
        {
            existing = ReadRemoteTablesViaInformationSchema();
        }
        catch (Exception ex) when (ShouldFallbackFromInformationSchema(ex))
        {
            existing = new Dictionary<string, SqlTableDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        if (existing.Count > 0)
            return existing;

        foreach (var table in modelSnapshot)
        {
            if (!TryReadRemoteColumns(table, out var columns))
                continue;

            existing[table.CollectionName] = new SqlTableDefinition(
                table.CollectionName,
                columns,
                Array.Empty<SqlIndexDefinition>(),
                Array.Empty<SqlForeignKeyDefinition>());
        }

        return existing;
    }

    private static bool ShouldFallbackFromInformationSchema(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (string.IsNullOrWhiteSpace(message))
                continue;

            if (current is ArgumentException argumentException
                && argumentException.Message.Contains("information_schema.columns", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (message.Contains("Unknown Collection information_schema.columns", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Collection name information_schema.columns is invalid", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // WalhallaSql engine error when information_schema tables don't exist yet
            if (message.Contains("Table 'information_schema.columns' not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Table 'information_schema.tables' not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("information_schema.columns", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private Dictionary<string, SqlTableDefinition> ReadRemoteTablesViaInformationSchema()
    {
        var rows = ExecuteSql("SELECT table_name, column_name, is_nullable, data_type FROM information_schema.columns WHERE table_schema = 'public' ORDER BY table_name, ordinal_position").Rows
            ?? Array.Empty<IReadOnlyDictionary<string, object?>>();

        var groupedColumns = new Dictionary<string, List<SqlColumnDefinition>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var tableName = row.TryGetValue("table_name", out var tableValue)
                ? tableValue?.ToString()
                : null;
            var columnName = row.TryGetValue("column_name", out var columnValue)
                ? columnValue?.ToString()
                : null;

            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(columnName))
                continue;

            if (!groupedColumns.TryGetValue(tableName, out var columns))
            {
                columns = new List<SqlColumnDefinition>();
                groupedColumns[tableName] = columns;
            }

            var nullableText = row.TryGetValue("is_nullable", out var nullableValue)
                ? nullableValue?.ToString()
                : null;
            var dataTypeText = row.TryGetValue("data_type", out var dataTypeValue)
                ? dataTypeValue?.ToString()
                : null;

            columns.Add(new SqlColumnDefinition(
                columnName,
                MapInformationSchemaType(dataTypeText),
                string.Equals(nullableText, "YES", StringComparison.OrdinalIgnoreCase),
                false,
                false));
        }

        var remoteTables = groupedColumns.ToDictionary(
            pair => pair.Key,
            pair => new SqlTableDefinition(
                pair.Key,
                pair.Value,
                Array.Empty<SqlIndexDefinition>(),
                Array.Empty<SqlForeignKeyDefinition>()),
            StringComparer.OrdinalIgnoreCase);

        EnrichRemoteTablesWithModelMetadata(remoteTables);
        return remoteTables;
    }

    private void EnrichRemoteTablesWithModelMetadata(Dictionary<string, SqlTableDefinition> remoteTables)
    {
        var modelTables = BuildModelSnapshot(_context.Model).ToList();

        foreach (var remoteTable in remoteTables.ToArray())
        {
            var matchingModel = modelTables
                .Where(model => RemoteAndModelTableColumnsAreCompatibleForEnrichment(remoteTable.Value, model))
                .ToList();

            if (matchingModel.Count != 1)
                continue;

            var model = matchingModel[0];
            var modelColumns = model.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
            var enrichedColumns = remoteTable.Value.Columns
                .Select(column => modelColumns.TryGetValue(column.Name, out var modelColumn)
                    ? new SqlColumnDefinition(column.Name, column.Type, column.IsNullable, modelColumn.IsPrimaryKey, modelColumn.IsUnique)
                    : column)
                .ToArray();

            remoteTables[remoteTable.Key] = new SqlTableDefinition(
                remoteTable.Value.CollectionName,
                enrichedColumns,
                remoteTable.Value.Indexes,
                remoteTable.Value.ForeignKeys);
        }
    }

    private static bool RemoteAndModelTableColumnsAreCompatibleForEnrichment(SqlTableDefinition remote, ModelTableSnapshot model)
    {
        if (remote.Columns.Count != model.Columns.Count)
            return false;

        var remoteColumns = remote.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var modelColumn in model.Columns)
        {
            if (!remoteColumns.TryGetValue(modelColumn.Name, out var remoteColumn))
                return false;

            if (remoteColumn.Type != modelColumn.Type)
                return false;
        }

        return true;
    }

    private bool TryReadRemoteColumns(ModelTableSnapshot table, out IReadOnlyList<SqlColumnDefinition> columns)
    {
        var command = _connection.CreateCommand();
        command.CommandText = $"SELECT TOP 1 * FROM {table.CollectionName}";

        try
        {
            using var reader = command.ExecuteReader();
            var schema = reader.GetSchemaTable();
            var list = new List<SqlColumnDefinition>(reader.FieldCount);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var clrType = reader.GetFieldType(i);
                var nullable = false;

                if (schema?.Rows.Count > i)
                {
                    var allowDbNullValue = schema.Rows[i]["AllowDBNull"];
                    if (allowDbNullValue is bool allowDbNull)
                        nullable = allowDbNull;
                }

                var modelColumn = table.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var isPrimaryKey = modelColumn?.IsPrimaryKey ?? false;
                list.Add(new SqlColumnDefinition(name, MapClrType(clrType), nullable && !isPrimaryKey, isPrimaryKey, false));
            }

            columns = list;
            return true;
        }
        catch
        {
            columns = Array.Empty<SqlColumnDefinition>();
            return false;
        }
        finally
        {
            command.Dispose();
        }
    }

    private IReadOnlyList<WalhallaSqlMigrationHistoryEntry> GetHistoryViaSql()
    {
        EnsureRemoteHistoryTable();

        var rows = ExecuteSql($"SELECT MigrationId, AppliedAtUtc, OperationCount FROM {HistoryCollectionName} ORDER BY AppliedAtUtc").Rows
            ?? Array.Empty<IReadOnlyDictionary<string, object?>>();

        return rows
            .Select(row => new WalhallaSqlMigrationHistoryEntry(
                row.TryGetValue("MigrationId", out var migrationId) ? migrationId?.ToString() ?? string.Empty : string.Empty,
                ParseHistoryDateTime(row.TryGetValue("AppliedAtUtc", out var appliedAt) ? appliedAt : null),
                ParseHistoryOperationCount(row.TryGetValue("OperationCount", out var operationCount) ? operationCount : null)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.MigrationId))
            .ToArray();
    }

    private static DateTime ParseHistoryDateTime(object? value)
    {
        if (value is DateTime dateTime)
            return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);

        if (value is string text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed.ToUniversalTime();

        return DateTime.UnixEpoch;
    }

    private static int ParseHistoryOperationCount(object? value)
    {
        if (value == null)
            return 0;

        if (value is int intValue)
            return intValue;

        if (value is long longValue)
            return Convert.ToInt32(longValue, CultureInfo.InvariantCulture);

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return 0;
    }

    private static SqlScalarType MapInformationSchemaType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
            return SqlScalarType.String;

        return dataType.Trim().ToLowerInvariant() switch
        {
            "smallint" => SqlScalarType.Int32,
            "integer" => SqlScalarType.Int32,
            "bigint" => SqlScalarType.Int64,
            "real" => SqlScalarType.Double,
            "double precision" => SqlScalarType.Double,
            "numeric" => SqlScalarType.Decimal,
            "decimal" => SqlScalarType.Decimal,
            "boolean" => SqlScalarType.Boolean,
            "date" => SqlScalarType.DateTime,
            "timestamp without time zone" => SqlScalarType.DateTime,
            "timestamp with time zone" => SqlScalarType.DateTime,
            "bytea" => SqlScalarType.Binary,
            "uuid" => SqlScalarType.String,
            "character varying" => SqlScalarType.String,
            "character" => SqlScalarType.String,
            "text" => SqlScalarType.String,
            _ => SqlScalarType.String
        };
    }

    private void ApplyPlanViaSql(WalhallaSqlMigrationPlan plan)
    {
        foreach (var operation in plan.Operations)
        {
            var sql = BuildMigrationSql(operation);
            ExecuteSql(sql);

            // Nach der SQL-Ausführung: Projektionen (JSON Virtual Columns) in die
            // Tabellen-Definition nachtragen, da sie nicht im SQL-DDL abgebildet werden.
            if (operation is CreateTableOperation createTable
                && createTable.Table.Projections is { Count: > 0 } projections)
            {
                var engine = _options.Engine;
                if (engine != null)
                {
                    var tableDef = engine.GetTableDefinition(createTable.Table.CollectionName);
                    if (tableDef != null)
                    {
                        var enrichedDef = tableDef with { Projections = projections };
                        engine.UpdateTableDefinition(createTable.Table.CollectionName, enrichedDef);
                    }
                }
            }
        }
    }

    public string GenerateCreateScript()
    {
        var tables = BuildModelSnapshot(_context.Model);
        var operations = new List<MigrationOperation>();
        var foreignKeys = new List<AddForeignKeyOperation>();

        foreach (var table in tables)
        {
            operations.Add(new CreateTableOperation(new SqlTableDefinition(
                table.CollectionName,
                table.Columns,
                Array.Empty<SqlIndexDefinition>(),
                Projections: table.Projections)));

            foreach (var index in table.Indexes)
                operations.Add(new CreateIndexOperation(table.CollectionName, index));

            foreach (var foreignKey in table.ForeignKeys)
                foreignKeys.Add(new AddForeignKeyOperation(table.CollectionName, foreignKey));
        }

        operations.AddRange(foreignKeys);

        var lines = operations
            .Select(operation => WalhallaSqlMigrationScriptBuilder.BuildMigrationSql(operation))
            .ToList();

        if (lines.Count == 0)
            return "-- No tables in the current model.";

        return string.Join(";\n", lines) + ";";
    }

    private string BuildMigrationSql(MigrationOperation operation)
    {
        return WalhallaSqlMigrationScriptBuilder.BuildMigrationSql(operation);
    }

    private void SaveHistoryViaSql(string migrationId, int operationCount)
    {
        EnsureRemoteHistoryTable();
        var exists = ExecuteSql($"SELECT MigrationId FROM {HistoryCollectionName} WHERE MigrationId = {SqlLiteral(migrationId)}").Rows
            ?? Array.Empty<IReadOnlyDictionary<string, object?>>();

        if (exists.Count > 0)
            return;

        var appliedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        ExecuteSql($"INSERT INTO {HistoryCollectionName} (MigrationId, AppliedAtUtc, OperationCount) VALUES ({SqlLiteral(migrationId)}, {SqlLiteral(appliedAt)}, {operationCount.ToString(CultureInfo.InvariantCulture)})");
    }

    private void EnsureRemoteHistoryTable()
    {
        try
        {
            ExecuteSql($"CREATE TABLE {HistoryCollectionName} (MigrationId VARCHAR(256) PRIMARY KEY, AppliedAtUtc DATETIME NOT NULL, OperationCount INT NOT NULL)");
        }
        catch (Exception ex) when (IsAlreadyExistsException(ex))
        {
        }
    }

    private SqlExecutionResult ExecuteSql(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        if (IsSelectSql(sql))
        {
            using var reader = command.ExecuteReader();
            var rows = new List<IReadOnlyDictionary<string, object?>>();

            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[reader.GetName(i)] = value == DBNull.Value ? null : value;
                }

                rows.Add(row);
            }

            return new SqlExecutionResult(rows.Count, rows);
        }

        var affected = command.ExecuteNonQuery();
        return new SqlExecutionResult(affected);
    }

    private static bool IsSelectSql(string sql)
    {
        return sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || sql.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    private static string SqlLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static bool IsAlreadyExistsException(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
               || message.Contains("existiert bereits", StringComparison.OrdinalIgnoreCase)
               || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }


    private IReadOnlyList<ModelTableSnapshot> BuildModelSnapshot(IModel model)
    {
        var tables = new Dictionary<string, ModelTableSnapshotBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in model.GetEntityTypes())
        {
            if (entity.ClrType == null)
                continue;

            var ownership = entity.FindOwnership();
            if (ownership != null && SharesTableWithPrincipal(entity, ownership, ResolveCollectionName))
                continue;

            ValidateSupportedEntityModel(entity);

            var collectionName = ResolveCollectionName(entity);
            if (!tables.TryGetValue(collectionName, out var table))
            {
                table = new ModelTableSnapshotBuilder(collectionName);
                tables[collectionName] = table;
            }

            var primaryKey = entity.FindPrimaryKey();
            var primaryColumns = primaryKey?.Properties
                .Select(property => GetColumnName(property))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in GetPersistedProperties(entity))
            {
                var columnName = GetColumnName(property);
                var sqlType = MapPropertyType(property);
                var isPrimary = primaryColumns.Contains(columnName);
                var isNullable = IsColumnNullable(property) && !isPrimary;
                table.AddColumn(new SqlColumnDefinition(columnName, sqlType, isNullable, isPrimary, false));
            }

            foreach (var jsonColumn in GetPersistedJsonContainerColumns(entity, primaryColumns))
                table.AddColumn(jsonColumn);

            table.AddProjections(GetPersistedJsonScalarProjections(entity));

            table.AddIndexes(BuildModelIndexes(entity, collectionName, table.Columns));
            table.AddForeignKeys(BuildModelForeignKeys(entity, collectionName, table.Columns));
        }

        return tables.Values
            .Where(table => table.Columns.Count > 0)
            .Select(table => table.Build())
            .ToArray();
    }

    private IReadOnlyList<IProperty> GetPersistedProperties(IEntityType entity)
    {
        var properties = new List<IProperty>();
        AppendPersistedProperties(entity, properties);
        return properties;
    }

    private void AppendPersistedProperties(IEntityType entity, ICollection<IProperty> properties)
    {
        if (entity.IsMappedToJson())
            return;

        var ownership = entity.FindOwnership();
        var skipOwnershipProperties = ownership != null
            && SharesTableWithPrincipal(entity, ownership, ResolveCollectionName);
        var ownershipProperties = skipOwnershipProperties ? ownership!.Properties : null;

        foreach (var property in entity.GetFlattenedProperties())
        {
            if (ownershipProperties != null && ownershipProperties.Contains(property))
                continue;

            properties.Add(property);
        }

        foreach (var navigation in entity.GetNavigations())
        {
            var targetEntity = navigation.TargetEntityType;
            var targetOwnership = targetEntity.FindOwnership();
            if (targetOwnership?.PrincipalEntityType != entity)
                continue;

            if (!SharesTableWithPrincipal(targetEntity, targetOwnership, ResolveCollectionName))
                continue;

            if (targetEntity.IsMappedToJson())
                continue;

            AppendPersistedProperties(targetEntity, properties);
        }
    }

    private IEnumerable<SqlColumnDefinition> GetPersistedJsonContainerColumns(
        IEntityType entity,
        IReadOnlySet<string> primaryColumns)
    {
        foreach (var navigation in entity.GetNavigations())
        {
            var targetEntity = navigation.TargetEntityType;
            var ownership = targetEntity.FindOwnership();
            if (ownership?.PrincipalEntityType != entity)
                continue;

            if (!SharesTableWithPrincipal(targetEntity, ownership, ResolveCollectionName))
                continue;

            if (targetEntity.IsMappedToJson())
            {
                var containerColumnName = targetEntity.GetContainerColumnName();
                if (string.IsNullOrEmpty(containerColumnName))
                    continue;

                var isPrimary = primaryColumns.Contains(containerColumnName);
                var isNullable = !ownership.IsRequiredDependent && !isPrimary;
                yield return new SqlColumnDefinition(containerColumnName, SqlScalarType.Json, isNullable, isPrimary, false);
                continue;
            }

            foreach (var nestedColumn in GetPersistedJsonContainerColumns(targetEntity, primaryColumns))
                yield return nestedColumn;
        }
    }

    private IEnumerable<SqlProjectionDefinition> GetPersistedJsonScalarProjections(IEntityType entity)
    {
        foreach (var complexProperty in entity.GetComplexProperties())
        {
            if (complexProperty.DeclaringType.IsMappedToJson())
                continue;

            var complexType = complexProperty.ComplexType;
            if (!complexType.IsMappedToJson())
                continue;

            var containerColumnName = complexType.GetContainerColumnName();
            if (string.IsNullOrEmpty(containerColumnName))
                continue;

            foreach (var projection in GetComplexJsonScalarProjections(complexType, containerColumnName, Array.Empty<string>()))
                yield return projection;
        }

        foreach (var navigation in entity.GetNavigations())
        {
            var targetEntity = navigation.TargetEntityType;
            var ownership = targetEntity.FindOwnership();
            if (ownership?.PrincipalEntityType != entity)
                continue;

            if (!SharesTableWithPrincipal(targetEntity, ownership, ResolveCollectionName))
                continue;

            if (targetEntity.IsMappedToJson())
            {
                var containerColumnName = targetEntity.GetContainerColumnName();
                if (string.IsNullOrEmpty(containerColumnName))
                    continue;

                foreach (var projection in GetEntityJsonScalarProjections(targetEntity, containerColumnName, Array.Empty<string>()))
                    yield return projection;

                continue;
            }

            foreach (var projection in GetPersistedJsonScalarProjections(targetEntity))
                yield return projection;
        }
    }

    private IEnumerable<SqlProjectionDefinition> GetEntityJsonScalarProjections(
        IEntityType entityType,
        string sourceColumnName,
        IReadOnlyList<string> pathPrefix)
    {
        foreach (var property in entityType.GetProperties())
        {
            if (property.IsPrimaryKey() || property.IsForeignKey() || property.IsShadowProperty())
                continue;

            var jsonPropertyName = property.GetJsonPropertyName() ?? property.Name;
            var path = pathPrefix.Concat([jsonPropertyName]).ToArray();
            yield return new SqlProjectionDefinition(
                WalhallaSqlJsonProjectionHelper.BuildProjectionName(sourceColumnName, path),
                sourceColumnName,
                path,
                MapPropertyType(property),
                SqlProjectionMaterializationMode.Virtual);
        }

        foreach (var navigation in entityType.GetNavigations())
        {
            if (!navigation.ForeignKey.IsOwnership || !navigation.TargetEntityType.IsMappedToJson())
                continue;

            var jsonPropertyName = navigation.TargetEntityType.GetJsonPropertyName() ?? navigation.Name;
            var nestedPath = pathPrefix.Concat([jsonPropertyName]).ToArray();
            foreach (var projection in GetEntityJsonScalarProjections(navigation.TargetEntityType, sourceColumnName, nestedPath))
                yield return projection;
        }
    }

    private IEnumerable<SqlProjectionDefinition> GetComplexJsonScalarProjections(
        IComplexType complexType,
        string sourceColumnName,
        IReadOnlyList<string> pathPrefix)
    {
        foreach (var property in complexType.GetProperties())
        {
            if (property.IsShadowProperty())
                continue;

            var jsonPropertyName = property.GetJsonPropertyName() ?? property.Name;
            var path = pathPrefix.Concat([jsonPropertyName]).ToArray();
            yield return new SqlProjectionDefinition(
                WalhallaSqlJsonProjectionHelper.BuildProjectionName(sourceColumnName, path),
                sourceColumnName,
                path,
                MapPropertyType(property),
                SqlProjectionMaterializationMode.Virtual);
        }

        foreach (var complexProperty in complexType.GetComplexProperties())
        {
            var jsonPropertyName = complexProperty.ComplexType.GetJsonPropertyName() ?? complexProperty.Name;
            var nestedPath = pathPrefix.Concat([jsonPropertyName]).ToArray();
            foreach (var projection in GetComplexJsonScalarProjections(complexProperty.ComplexType, sourceColumnName, nestedPath))
                yield return projection;
        }
    }

    private static bool SharesTableWithPrincipal(
        IEntityType entity,
        IForeignKey ownership,
        Func<IEntityType, string> collectionNameResolver)
    {
        var entityCollection = collectionNameResolver(entity);
        var principalCollection = collectionNameResolver(ownership.PrincipalEntityType);
        return string.Equals(entityCollection, principalCollection, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveCollectionName(IEntityType entity)
    {
        if (_options.CollectionNameResolver != null)
            return _options.CollectionNameResolver(entity);

        return WalhallaSqlStoreObjectNameSanitizer.ResolveDefaultCollectionName(entity);
    }

    /// <summary>
    /// Returns the DB column name for a property, respecting .HasColumnName("...") configuration.
    /// Falls back to the CLR property name when no explicit column name is mapped.
    /// </summary>
    private static string GetColumnName(IProperty property)
    {
        var storeObject = TryResolveStoreObject(property);
        if (storeObject.HasValue)
        {
            var relationalName = property.GetColumnName(storeObject.Value);
            if (!string.IsNullOrEmpty(relationalName))
                return relationalName;
        }

        var columnName = property.FindAnnotation("Relational:ColumnName")?.Value as string;
        return string.IsNullOrEmpty(columnName) ? property.Name : columnName;
    }

    private static StoreObjectIdentifier? TryResolveStoreObject(IProperty property)
    {
        var typeBase = property.DeclaringType;
        while (typeBase is IComplexType complexType)
            typeBase = complexType.ComplexProperty.DeclaringType;

        if (typeBase is not IEntityType entityType)
            return null;

        var tableName = entityType.GetTableName();
        return string.IsNullOrEmpty(tableName)
            ? null
            : StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
    }

    private static bool IsColumnNullable(IProperty property)
    {
        var declaringEntityType = property.DeclaringType as IEntityType;
        if (declaringEntityType != null
            && !property.IsPrimaryKey()
            && HasOptionalSharedTableDependentMapping(declaringEntityType))
        {
            return true;
        }

        var tableName = declaringEntityType?.GetTableName();
        if (!string.IsNullOrEmpty(tableName))
        {
            var storeObject = StoreObjectIdentifier.Table(tableName, declaringEntityType!.GetSchema());
            if (property.GetColumnName(storeObject) != null)
                return property.IsColumnNullable(storeObject);
        }

        return property.IsNullable;
    }

    private static bool HasOptionalSharedTableDependentMapping(IEntityType entityType)
    {
        return entityType.Model.GetEntityTypes().Any(candidate =>
            SharesTable(candidate, entityType)
            && IsSameOrDerivedType(candidate, entityType)
            && IsOptionalSharedTableDependent(candidate));
    }

    private static bool IsOptionalSharedTableDependent(IEntityType entityType)
    {
        foreach (var foreignKey in entityType.GetForeignKeys())
        {
            if (foreignKey.IsOwnership || !foreignKey.IsUnique || foreignKey.IsRequiredDependent)
                continue;

            if (!foreignKey.Properties.All(property => property.IsPrimaryKey()))
                continue;

            var dependentTable = entityType.GetTableName();
            var principalTable = foreignKey.PrincipalEntityType.GetTableName();
            if (string.IsNullOrWhiteSpace(dependentTable)
                || string.IsNullOrWhiteSpace(principalTable)
                || !string.Equals(dependentTable, principalTable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dependentSchema = entityType.GetSchema();
            var principalSchema = foreignKey.PrincipalEntityType.GetSchema();
            if (string.Equals(dependentSchema, principalSchema, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool SharesTable(IEntityType left, IEntityType right)
    {
        var leftTable = left.GetTableName();
        var rightTable = right.GetTableName();
        if (string.IsNullOrWhiteSpace(leftTable)
            || string.IsNullOrWhiteSpace(rightTable)
            || !string.Equals(leftTable, rightTable, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(left.GetSchema(), right.GetSchema(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrDerivedType(IEntityType candidate, IEntityType baseType)
    {
        for (var current = candidate; current != null; current = current.BaseType)
        {
            if (ReferenceEquals(current, baseType))
                return true;
        }

        return false;
    }

    private static SqlScalarType MapClrType(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type.IsEnum)
            return MapClrType(Enum.GetUnderlyingType(type));

        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int))
            return SqlScalarType.Int32;

        if (type == typeof(uint) || type == typeof(long))
            return SqlScalarType.Int64;

        if (type == typeof(ulong) || type == typeof(decimal))
            return SqlScalarType.Decimal;

        if (type == typeof(float) || type == typeof(double))
            return SqlScalarType.Double;

        if (type == typeof(bool))
            return SqlScalarType.Boolean;

        if (type == typeof(char))
            return SqlScalarType.String;

        if (type == typeof(DateTime))
            return SqlScalarType.DateTime;

        if (type == typeof(DateTimeOffset))
            return SqlScalarType.String;

        if (type == typeof(byte[]))
            return SqlScalarType.Binary;

        return SqlScalarType.String;
    }

    private static SqlScalarType MapPropertyType(IProperty property)
    {
        var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;
        var providerClrType = converter?.ProviderClrType
            ?? property.GetProviderClrType()
            ?? property.ClrType;

        return MapClrType(providerClrType);
    }

    private static string? GetDefaultLiteralForAddedColumn(SqlColumnDefinition column)
    {
        if (column.IsNullable)
            return null;

        return column.Type switch
        {
            SqlScalarType.Int32 => "0",
            SqlScalarType.Int64 => "0",
            SqlScalarType.Double => "0",
            SqlScalarType.Boolean => "FALSE",
            SqlScalarType.DateTime => $"'{DateTime.UnixEpoch.ToString("O", CultureInfo.InvariantCulture)}'",
            SqlScalarType.Guid => $"'{Guid.Empty.ToString("D", CultureInfo.InvariantCulture)}'",
            SqlScalarType.Binary => "''",
            SqlScalarType.String => "''",
            _ => null
        };
    }

    private static bool HaveCompatibleTableShapeForRename(SqlTableDefinition existing, ModelTableSnapshot model)
    {
        if (existing.Columns.Count != model.Columns.Count)
            return false;

        // Require ALL column names and types to match exactly.
        // This prevents false-positive rename detection between tables that merely
        // share the same column-type signature but have different column names.
        var existingByName = existing.Columns
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var modelColumn in model.Columns)
        {
            if (!existingByName.TryGetValue(modelColumn.Name, out var existingColumn))
                return false;

            if (existingColumn.Type != modelColumn.Type
                || existingColumn.IsNullable != modelColumn.IsNullable
                || existingColumn.IsPrimaryKey != modelColumn.IsPrimaryKey
                || existingColumn.IsUnique != modelColumn.IsUnique)
                return false;
        }

        return true;
    }

    private static Dictionary<(SqlScalarType Type, bool IsNullable, bool IsPrimaryKey, bool IsUnique), int> BuildColumnSignatureMap(
        IEnumerable<SqlColumnDefinition> columns)
    {
        var map = new Dictionary<(SqlScalarType Type, bool IsNullable, bool IsPrimaryKey, bool IsUnique), int>();

        foreach (var column in columns)
        {
            var key = (column.Type, column.IsNullable, column.IsPrimaryKey, column.IsUnique);
            if (!map.TryAdd(key, 1))
                map[key]++;
        }

        return map;
    }

    private static bool AreColumnsRenameCompatible(SqlColumnDefinition existing, SqlColumnDefinition replacement)
    {
        return replacement.Type == existing.Type
            && replacement.IsNullable == existing.IsNullable
            && replacement.IsPrimaryKey == existing.IsPrimaryKey
            && replacement.IsUnique == existing.IsUnique;
    }

    private static void ValidateSupportedAlterColumnChange(
        string collectionName,
        SqlColumnDefinition existing,
        SqlColumnDefinition target)
    {
        if (existing.IsNullable && !target.IsNullable)
        {
            throw new NotSupportedException(
                $"Altering column '{collectionName}.{target.Name}' from NULLABLE to NOT NULL is blocked by safety guardrails. " +
                "Backfill null values first and run an explicit SQL migration step.");
        }

        if (IsPotentiallyLossyOrAmbiguousTypeChange(existing.Type, target.Type))
        {
            throw new NotSupportedException(
                $"Altering column '{collectionName}.{target.Name}' from {existing.Type} to {target.Type} is blocked by safety guardrails. " +
                "Use explicit SQL migration with data validation/conversion.");
        }
    }

    private static bool IsPotentiallyLossyOrAmbiguousTypeChange(SqlScalarType sourceType, SqlScalarType targetType)
    {
        if (sourceType == targetType)
            return false;

        if (sourceType == SqlScalarType.Unknown || targetType == SqlScalarType.Unknown)
            return true;

        if (targetType == SqlScalarType.String)
            return false;

        if (sourceType == SqlScalarType.String)
            return true;

        if (sourceType == SqlScalarType.Binary || targetType == SqlScalarType.Binary)
            return true;

        if (sourceType == SqlScalarType.DateTime || targetType == SqlScalarType.DateTime)
            return true;

        if (sourceType == SqlScalarType.Boolean || targetType == SqlScalarType.Boolean)
            return true;

        if (IsNumericType(sourceType) && IsNumericType(targetType))
        {
            if (sourceType == SqlScalarType.Int32 && (targetType == SqlScalarType.Int64 || targetType == SqlScalarType.Double))
                return false;

            return true;
        }

        return true;
    }

    private static bool IsNumericType(SqlScalarType type)
    {
        return type == SqlScalarType.Int32
            || type == SqlScalarType.Int64
            || type == SqlScalarType.Double;
    }

    private static void ValidateSupportedEntityModel(IEntityType entity)
    {
    }

    private static IReadOnlyList<SqlIndexDefinition> BuildModelIndexes(
        IEntityType entity,
        string collectionName,
        IReadOnlyList<SqlColumnDefinition> columns)
    {
        var indexesBySignature = new Dictionary<string, SqlIndexDefinition>(StringComparer.OrdinalIgnoreCase);
        var availableColumns = new HashSet<string>(columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns.Where(column => column.IsPrimaryKey || column.IsUnique))
        {
            var implicitIndex = new SqlIndexDefinition(
                GetImplicitIndexName(collectionName, column.Name),
                column.Name,
                column.IsUnique);
            indexesBySignature[GetIndexSignature(implicitIndex.ColumnNames)] = implicitIndex;
        }

        foreach (var alternateKey in entity.GetKeys().Where(key => key != entity.FindPrimaryKey()))
        {
            var columnNames = alternateKey.Properties.Select(GetColumnName).ToArray();
            if (columnNames.Any(columnName => !availableColumns.Contains(columnName)))
                continue;

            var keyName = alternateKey.GetName();
            if (string.IsNullOrWhiteSpace(keyName))
                keyName = $"AK_{collectionName}_{string.Join("_", columnNames)}";

            var alternateKeyIndex = new SqlIndexDefinition(keyName, columnNames, isUnique: true);
            indexesBySignature[GetIndexSignature(alternateKeyIndex.ColumnNames)] = alternateKeyIndex;
        }

        foreach (var index in entity.GetIndexes())
        {
            var columnNames = index.Properties.Select(GetColumnName).ToArray();
            if (columnNames.Any(columnName => !availableColumns.Contains(columnName)))
                continue;

            var indexName = index.GetDatabaseName();

            if (string.IsNullOrWhiteSpace(indexName))
                indexName = GetImplicitIndexName(collectionName, string.Join("_", columnNames));

            var modelIndex = new SqlIndexDefinition(indexName, columnNames, index.IsUnique);
            indexesBySignature[GetIndexSignature(modelIndex.ColumnNames)] = modelIndex;
        }

        return indexesBySignature.Values.ToArray();
    }

    private IReadOnlyList<SqlForeignKeyDefinition> BuildModelForeignKeys(
        IEntityType entity,
        string collectionName,
        IReadOnlyList<SqlColumnDefinition> columns)
    {
        var foreignKeys = new List<SqlForeignKeyDefinition>();
        var availableColumns = new HashSet<string>(columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var foreignKey in entity.GetForeignKeys())
        {
            if (foreignKey.Properties.Count != foreignKey.PrincipalKey.Properties.Count)
            {
                throw new NotSupportedException(
                    $"Entity '{entity.Name}' defines a foreign key with mismatching child/principal column counts, which is not supported by WalhallaSql EF migrations.");
            }

            if (!IsSupportedDeleteBehavior(foreignKey.DeleteBehavior))
            {
                throw new NotSupportedException(
                    $"Entity '{entity.Name}' defines foreign key delete behavior '{foreignKey.DeleteBehavior}', which is not supported by WalhallaSql EF migrations.");
            }

            var childProperties = foreignKey.Properties
                .Select(property => GetColumnName(property))
                .ToArray();
            if (childProperties.Any(columnName => !availableColumns.Contains(columnName)))
                continue;

            var principalEntity = foreignKey.PrincipalEntityType;
            var principalCollection = ResolveCollectionName(principalEntity);
            var principalProperties = foreignKey.PrincipalKey.Properties
                .Select(property => GetColumnName(property))
                .ToArray();

            var constraintName = foreignKey.GetConstraintName();
            if (string.IsNullOrWhiteSpace(constraintName))
                constraintName = $"FK_{collectionName}_{string.Join("_", childProperties)}_{principalCollection}_{string.Join("_", principalProperties)}";

            foreignKeys.Add(new SqlForeignKeyDefinition(
                constraintName,
                childProperties,
                principalCollection,
                principalProperties,
                MapDeleteBehavior(foreignKey.DeleteBehavior),
                SqlForeignKeyAction.Restrict));
        }

        return foreignKeys;
    }

    private static IReadOnlyList<SqlIndexDefinition> GetAdditionalIndexesForNewTable(ModelTableSnapshot table)
    {
        return table.Indexes
            .Where(index => index.ColumnNames.Count > 1 || !table.Columns.Any(column =>
                column.Name.Equals(index.ColumnName, StringComparison.OrdinalIgnoreCase)
                && (column.IsPrimaryKey || column.IsUnique)))
            .ToArray();
    }

    private static void AddIndexDiffOperations(
        List<MigrationOperation> operations,
        ModelTableSnapshot modelTable,
        SqlTableDefinition currentTable)
    {
        var currentBySignature = currentTable.Indexes
            .GroupBy(index => GetIndexSignature(index.ColumnNames), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var modelBySignature = modelTable.Indexes
            .GroupBy(index => GetIndexSignature(index.ColumnNames), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var current in currentBySignature)
        {
            if (!modelBySignature.TryGetValue(current.Key, out var modelIndex))
            {
                operations.Add(new DropIndexOperation(modelTable.CollectionName, current.Value.IndexName));
                continue;
            }

            if (string.Equals(current.Value.IndexName, modelIndex.IndexName, StringComparison.OrdinalIgnoreCase)
                && current.Value.IsUnique == modelIndex.IsUnique)
            {
                continue;
            }

            operations.Add(new DropIndexOperation(modelTable.CollectionName, current.Value.IndexName));
            operations.Add(new CreateIndexOperation(modelTable.CollectionName, modelIndex));
        }

        foreach (var model in modelBySignature)
        {
            if (!currentBySignature.ContainsKey(model.Key))
                operations.Add(new CreateIndexOperation(modelTable.CollectionName, model.Value));
        }
    }

    private static string GetIndexSignature(IReadOnlyList<string> columnNames)
    {
        return string.Join("|", columnNames.Select(columnName => columnName.ToUpperInvariant()));
    }

    private static void AddForeignKeyDropOperations(
        List<MigrationOperation> operations,
        ModelTableSnapshot modelTable,
        SqlTableDefinition currentTable)
    {
        var currentByName = GetForeignKeys(currentTable)
            .ToDictionary(foreignKey => foreignKey.ConstraintName, StringComparer.OrdinalIgnoreCase);

        var modelByName = modelTable.ForeignKeys
            .ToDictionary(foreignKey => foreignKey.ConstraintName, StringComparer.OrdinalIgnoreCase);

        foreach (var current in currentByName)
        {
            if (!modelByName.TryGetValue(current.Key, out var modelForeignKey)
                || !AreForeignKeysEquivalent(current.Value, modelForeignKey))
            {
                operations.Add(new DropForeignKeyOperation(modelTable.CollectionName, current.Value.ConstraintName));
            }
        }
    }

    private static void AddForeignKeyAddOperations(
        List<MigrationOperation> operations,
        ModelTableSnapshot modelTable,
        SqlTableDefinition currentTable)
    {
        var currentByName = GetForeignKeys(currentTable)
            .ToDictionary(foreignKey => foreignKey.ConstraintName, StringComparer.OrdinalIgnoreCase);

        foreach (var modelForeignKey in modelTable.ForeignKeys)
        {
            if (!currentByName.TryGetValue(modelForeignKey.ConstraintName, out var currentForeignKey)
                || !AreForeignKeysEquivalent(currentForeignKey, modelForeignKey))
            {
                operations.Add(new AddForeignKeyOperation(modelTable.CollectionName, modelForeignKey));
            }
        }
    }

    private static bool AreForeignKeysEquivalent(SqlForeignKeyDefinition left, SqlForeignKeyDefinition right)
    {
        return left.ConstraintName.Equals(right.ConstraintName, StringComparison.OrdinalIgnoreCase)
            && left.ColumnNames.SequenceEqual(right.ColumnNames, StringComparer.OrdinalIgnoreCase)
            && left.ReferencedCollection.Equals(right.ReferencedCollection, StringComparison.OrdinalIgnoreCase)
            && left.ReferencedColumns.SequenceEqual(right.ReferencedColumns, StringComparer.OrdinalIgnoreCase)
            && left.OnDelete == right.OnDelete
            && left.OnUpdate == right.OnUpdate;
    }

    private static IReadOnlyList<SqlForeignKeyDefinition> GetForeignKeys(SqlTableDefinition table)
    {
        return table.ForeignKeys ?? Array.Empty<SqlForeignKeyDefinition>();
    }

    private static bool IsSupportedDeleteBehavior(DeleteBehavior deleteBehavior)
    {
        return deleteBehavior == DeleteBehavior.Restrict
            || deleteBehavior == DeleteBehavior.NoAction
            || deleteBehavior == DeleteBehavior.SetNull
            || deleteBehavior == DeleteBehavior.ClientSetNull
            || deleteBehavior == DeleteBehavior.ClientNoAction
            || deleteBehavior == DeleteBehavior.Cascade
            || deleteBehavior == DeleteBehavior.ClientCascade;
    }

    private static SqlForeignKeyAction MapDeleteBehavior(DeleteBehavior deleteBehavior)
    {
        return deleteBehavior switch
        {
            DeleteBehavior.Cascade => SqlForeignKeyAction.Cascade,
            DeleteBehavior.ClientCascade => SqlForeignKeyAction.Cascade,
            DeleteBehavior.Restrict => SqlForeignKeyAction.Restrict,
            DeleteBehavior.NoAction => SqlForeignKeyAction.Restrict,
            DeleteBehavior.SetNull => SqlForeignKeyAction.SetNull,
            DeleteBehavior.ClientSetNull => SqlForeignKeyAction.SetNull,
            DeleteBehavior.ClientNoAction => SqlForeignKeyAction.Restrict,
            _ => throw new NotSupportedException($"DeleteBehavior '{deleteBehavior}' is not supported by WalhallaSql EF migrations.")
        };
    }

    private static string GetImplicitIndexName(string collectionName, string columnName)
    {
        return $"IX_{collectionName}_{columnName}";
    }


    private sealed class ModelTableSnapshotBuilder
    {
        private readonly Dictionary<string, SqlColumnDefinition> _columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SqlIndexDefinition> _indexes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SqlForeignKeyDefinition> _foreignKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SqlProjectionDefinition> _projections = new(StringComparer.OrdinalIgnoreCase);

        public ModelTableSnapshotBuilder(string collectionName)
        {
            CollectionName = collectionName;
        }

        public string CollectionName { get; }

        public IReadOnlyList<SqlColumnDefinition> Columns => _columns.Values.ToArray();

        public IReadOnlyList<SqlProjectionDefinition> Projections => _projections.Values.ToArray();

        public void AddColumn(SqlColumnDefinition column)
        {
            if (_columns.TryGetValue(column.Name, out var existing))
            {
                if (existing.Type != column.Type)
                {
                    throw new NotSupportedException(
                        $"Collection '{CollectionName}' maps column '{column.Name}' with conflicting provider types ('{existing.Type}' vs. '{column.Type}').");
                }

                _columns[column.Name] = existing with
                {
                    IsNullable = existing.IsNullable || column.IsNullable,
                    IsPrimaryKey = existing.IsPrimaryKey || column.IsPrimaryKey,
                    IsUnique = existing.IsUnique || column.IsUnique
                };
                return;
            }

            _columns[column.Name] = column;
        }

        public void AddIndexes(IEnumerable<SqlIndexDefinition> indexes)
        {
            foreach (var index in indexes)
            {
                var signature = GetIndexSignature(index.ColumnNames);
                if (_indexes.TryGetValue(signature, out var existing))
                {
                    if (existing.IsUnique != index.IsUnique)
                    {
                        throw new NotSupportedException(
                            $"Collection '{CollectionName}' defines conflicting uniqueness for index columns '{string.Join(", ", index.ColumnNames)}'.");
                    }

                    continue;
                }

                _indexes[signature] = index;
            }
        }

        public void AddForeignKeys(IEnumerable<SqlForeignKeyDefinition> foreignKeys)
        {
            foreach (var foreignKey in foreignKeys)
            {
                if (_foreignKeys.TryGetValue(foreignKey.ConstraintName, out var existing))
                {
                    if (!AreForeignKeysEquivalent(existing, foreignKey))
                    {
                        throw new NotSupportedException(
                            $"Collection '{CollectionName}' defines conflicting foreign key '{foreignKey.ConstraintName}'.");
                    }

                    continue;
                }

                _foreignKeys[foreignKey.ConstraintName] = foreignKey;
            }
        }

        public void AddProjections(IEnumerable<SqlProjectionDefinition> projections)
        {
            foreach (var projection in projections)
            {
                if (_projections.TryGetValue(projection.ProjectionName, out var existing))
                {
                    if (!string.Equals(existing.SourceColumnName, projection.SourceColumnName, StringComparison.OrdinalIgnoreCase)
                        || existing.ResultType != projection.ResultType
                        || !existing.PathSegments.SequenceEqual(projection.PathSegments, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException(
                            $"Collection '{CollectionName}' defines conflicting JSON projection '{projection.ProjectionName}'.");
                    }

                    continue;
                }

                _projections[projection.ProjectionName] = projection;
            }
        }

        public ModelTableSnapshot Build()
        {
            return new ModelTableSnapshot(
                CollectionName,
                _columns.Values.ToArray(),
                _indexes.Values.ToArray(),
                _foreignKeys.Values.ToArray(),
                _projections.Values.ToArray());
        }
    }

    private sealed record ModelTableSnapshot(
        string CollectionName,
        IReadOnlyList<SqlColumnDefinition> Columns,
        IReadOnlyList<SqlIndexDefinition> Indexes,
        IReadOnlyList<SqlForeignKeyDefinition> ForeignKeys,
        IReadOnlyList<SqlProjectionDefinition> Projections);
}
