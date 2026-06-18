using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WalhallaSql.AdoNet;
using WalhallaSql.EfCore.Migrations;
using WalhallaSql.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.Logging;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlDbContextRuntime : IDisposable, IAsyncDisposable
{
    private readonly DbContext _context;
    private readonly WalhallaSqlEfCoreOptions _layeredOptions;
    private readonly WalhallaSqlDbConnection _sqlConnection;
    private readonly bool _ownsConnection;
    private WalhallaSqlMigrationService? _migrationService;

    private WalhallaSqlDbContextRuntime(
        DbContext context,
        WalhallaSqlEfCoreOptions layeredOptions,
        WalhallaSqlDbConnection sqlConnection,
        bool ownsConnection)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _layeredOptions = layeredOptions ?? throw new ArgumentNullException(nameof(layeredOptions));
        _sqlConnection = sqlConnection ?? throw new ArgumentNullException(nameof(sqlConnection));
        _ownsConnection = ownsConnection;
    }

    public WalhallaSqlEfCoreOptions Options => _layeredOptions;

    public WalhallaSqlMigrationService Migrations =>
        _migrationService ??= new WalhallaSqlMigrationService(
            _context,
            _sqlConnection,
            _layeredOptions,
            logger: ResolveLogger(_context));

    private static ILogger<WalhallaSqlMigrationService>? ResolveLogger(DbContext context)
    {
        try
        {
            var loggerFactory = context.GetService<ILoggerFactory>();
            return loggerFactory?.CreateLogger<WalhallaSqlMigrationService>();
        }
        catch
        {
            return null;
        }
    }
    public static WalhallaSqlDbContextRuntime Create(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var contextOptions = context.GetService<IDbContextOptions>();
        var layeredOptions = ResolveLayeredOptions(contextOptions);
        var existingConnection = contextOptions.FindExtension<WalhallaSqlDbContextOptionsExtension>()?.Connection
            as WalhallaSqlDbConnection;
        var (sqlConnection, ownsConnection) = CreateExecutionContext(layeredOptions, existingConnection);
        return new WalhallaSqlDbContextRuntime(context, layeredOptions, sqlConnection, ownsConnection);
    }

    public SqlExecutionResult ExecuteSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL must not be empty.", nameof(sql));

        return ExecuteSqlInternal(sql, transaction: null);
    }

    public int SaveChanges(IList<IUpdateEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var changedEntries = entries
            .Where(entry => entry.EntityState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        if (changedEntries.Length == 0)
            return 0;

        var entityEntries = changedEntries
            .Select(entry => (UpdateEntry: entry, EntityEntry: entry.ToEntityEntry()))
            .ToArray();

        ValidateSaveChangesEntries(entityEntries);
        var addedEntries = changedEntries.Where(entry => entry.EntityState == EntityState.Added).ToArray();
        var generatedKeyChanges = MaterializeGeneratedKeys(addedEntries);
        PropagateGeneratedForeignKeys(entityEntries.Select(pair => pair.EntityEntry).ToArray(), generatedKeyChanges);
        var workItems = BuildSaveChangesWorkItems(entityEntries);
        var hasAddedGraphDependencies = HasAddedGraphDependencies(workItems);
        var hasDeleteGraphDependencies = HasDeleteGraphDependencies(workItems);

        var externalTransaction = _context.Database.CurrentTransaction == null
            ? null
            : Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(_context.Database.CurrentTransaction);
        DbTransaction? transaction = externalTransaction;
        var ownsTransaction = false;
        var canUseImplicitTransaction = !hasAddedGraphDependencies
            && !hasDeleteGraphDependencies;

        if (transaction == null && canUseImplicitTransaction)
        {
            try
            {
                transaction = _sqlConnection.BeginTransaction();
                ownsTransaction = true;
            }
            catch (NotSupportedException)
            {
                transaction = null;
            }
        }

        var updateLogger = _context.GetService<IDiagnosticsLogger<DbLoggerCategory.Update>>();

        try
        {
            var writtenEntities = 0;

            foreach (var workItem in workItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var commands = BuildCommandsForWorkItem(workItem);
                if (commands.Count == 0)
                    continue;

                if (workItem.EffectiveState == EntityState.Added)
                    ValidateNoDuplicateKey(workItem, transaction);

                SqlExecutionResult? lastResult = null;
                foreach (var command in commands)
                {
                    var result = ExecuteSqlInternal(command.Sql, transaction);

                    if (command.RequiresAffectedRow && result.AffectedRows == 0)
                    {
                        var exception = CreateOptimisticConcurrencyException(workItem, command);
                        var interceptionResult = updateLogger.OptimisticConcurrencyException(
                            _context,
                            GetConcurrencyEntries(workItem),
                            exception,
                            null);

                        if (!interceptionResult.IsSuppressed)
                            throw exception;
                    }

                    lastResult = result;
                }

                writtenEntities++;
                if (_context is WalhallaSqlEfCoreContext layeredContext && lastResult != null)
                    layeredContext.NotifySaveChangesCommandExecuted(workItem.RootEntityEntry, lastResult);
            }

            if (ownsTransaction)
                transaction?.Commit();
            return writtenEntities;
        }
        catch
        {
            if (ownsTransaction)
                transaction?.Rollback();
            throw;
        }
        finally
        {
            if (ownsTransaction)
                transaction?.Dispose();
        }
    }

    public async Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var changedEntries = entries
            .Where(entry => entry.EntityState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        if (changedEntries.Length == 0)
            return 0;

        var entityEntries = changedEntries
            .Select(entry => (UpdateEntry: entry, EntityEntry: entry.ToEntityEntry()))
            .ToArray();

        ValidateSaveChangesEntries(entityEntries);
        var addedEntries = changedEntries.Where(entry => entry.EntityState == EntityState.Added).ToArray();
        var generatedKeyChanges = MaterializeGeneratedKeys(addedEntries);
        PropagateGeneratedForeignKeys(entityEntries.Select(pair => pair.EntityEntry).ToArray(), generatedKeyChanges);
        var workItems = BuildSaveChangesWorkItems(entityEntries);
        var hasAddedGraphDependencies = HasAddedGraphDependencies(workItems);
        var hasDeleteGraphDependencies = HasDeleteGraphDependencies(workItems);

        var externalTransaction = _context.Database.CurrentTransaction == null
            ? null
            : Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(_context.Database.CurrentTransaction);
        DbTransaction? transaction = externalTransaction;
        var ownsTransaction = false;
        var canUseImplicitTransaction = !hasAddedGraphDependencies
            && !hasDeleteGraphDependencies;

        if (transaction == null && canUseImplicitTransaction)
        {
            try
            {
                transaction = _sqlConnection.BeginTransaction();
                ownsTransaction = true;
            }
            catch (NotSupportedException)
            {
                transaction = null;
            }
        }

        var updateLogger = _context.GetService<IDiagnosticsLogger<DbLoggerCategory.Update>>();

        try
        {
            var writtenEntities = 0;

            foreach (var workItem in workItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var commands = BuildCommandsForWorkItem(workItem);
                if (commands.Count == 0)
                    continue;

                if (workItem.EffectiveState == EntityState.Added)
                    ValidateNoDuplicateKey(workItem, transaction);

                SqlExecutionResult? lastResult = null;
                foreach (var command in commands)
                {
                    var result = ExecuteSqlInternal(command.Sql, transaction);

                    if (command.RequiresAffectedRow && result.AffectedRows == 0)
                    {
                        var exception = CreateOptimisticConcurrencyException(workItem, command);
                        var interceptionResult = await updateLogger.OptimisticConcurrencyExceptionAsync(
                                _context,
                                GetConcurrencyEntries(workItem),
                                exception,
                                null,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (!interceptionResult.IsSuppressed)
                            throw exception;
                    }

                    lastResult = result;
                }

                writtenEntities++;
                if (_context is WalhallaSqlEfCoreContext layeredContext && lastResult != null)
                    layeredContext.NotifySaveChangesCommandExecuted(workItem.RootEntityEntry, lastResult);
            }

            if (ownsTransaction)
                transaction?.Commit();
            return writtenEntities;
        }
        catch
        {
            if (ownsTransaction)
                transaction?.Rollback();
            throw;
        }
        finally
        {
            if (ownsTransaction)
                transaction?.Dispose();
        }
    }

    public string ResolveCollectionName(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        if (_layeredOptions.CollectionNameResolver != null)
            return _layeredOptions.CollectionNameResolver(entityType);

        return WalhallaSqlStoreObjectNameSanitizer.ResolveDefaultCollectionName(entityType);
    }

    public void Dispose()
    {
        if (_ownsConnection)
            _sqlConnection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsConnection)
            _sqlConnection.Dispose();
        return ValueTask.CompletedTask;
    }

    private SqlExecutionResult ExecuteSqlInternal(string sql, DbTransaction? transaction)
    {
        using var command = _sqlConnection.CreateCommand();
        command.CommandText = sql;
        if (transaction != null)
            command.Transaction = transaction;

        if (IsSelectSql(sql))
        {
            using var reader = command.ExecuteReader();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var projectedColumns = ExtractSelectOutputColumns(sql);

            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    var columnName = projectedColumns != null && i < projectedColumns.Count
                        ? projectedColumns[i]
                        : reader.GetName(i);

                    row[columnName] = value == DBNull.Value ? null : value;
                }

                rows.Add(row);
            }

            return new SqlExecutionResult(rows.Count, rows) { CommandText = sql };
        }

        var affected = command.ExecuteNonQuery();
        return new SqlExecutionResult(affected) { CommandText = sql };
    }

    private IReadOnlyList<SaveChangesCommand> BuildCommandsForWorkItem(SaveChangesWorkItem workItem)
    {
        return workItem.EffectiveState switch
        {
            EntityState.Added => BuildInsertCommands(workItem),
            EntityState.Modified => new[] { BuildUpdateCommand(workItem) },
            EntityState.Deleted => new[] { BuildDeleteCommand(workItem) },
            _ => Array.Empty<SaveChangesCommand>()
        };
    }

    private IReadOnlyList<SaveChangesCommand> BuildInsertCommands(SaveChangesWorkItem workItem)
    {
        var entityEntry = workItem.RootEntityEntry;
        var commands = new List<SaveChangesCommand>();

        foreach (var storeObject in GetInsertStoreObjects(workItem))
        {
            var collectionName = ResolveInsertCollectionName(workItem, storeObject);

            foreach (var entryGroup in BuildInsertEntryGroups(workItem, storeObject))
            {
                var properties = GetInsertProperties(entryGroup, storeObject);
                var columnValues = properties
                    .Select(property => new StoreColumnValue(GetColumnName(property.Property, storeObject), property.Value, property.Property))
                    .Concat(GetJsonContainerColumnValues(workItem, storeObject))
                    .GroupBy(value => value.ColumnName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .ToArray();

                if (columnValues.Length == 0)
                    continue;

                var columns = string.Join(", ", columnValues.Select(value => value.ColumnName));
                var values = string.Join(", ", columnValues.Select(value => WalhallaSqlEfCoreSqlRenderer.FormatSqlLiteral(value.Value, value.Property)));

                commands.Add(new SaveChangesCommand(
                    $"INSERT INTO {collectionName} ({columns}) VALUES ({values})",
                    RequiresAffectedRow: false,
                    KeyDescription: null));
            }
        }

        if (commands.Count == 0)
        {
            throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.NoMappedScalarProperties,
                $"SaveChanges INSERT for '{entityEntry.Metadata.Name}' requires at least one mapped scalar property.",
                "Map at least one scalar property or avoid persisting this entity via SaveChanges MVP path.");
        }

        return commands;
    }

    private SaveChangesCommand BuildUpdateCommand(SaveChangesWorkItem workItem)
    {
        var entityEntry = workItem.RootEntityEntry;
        var collectionName = ResolveCollectionName(entityEntry.Metadata);
        var keyProperties = GetPrimaryKeyProperties(entityEntry);
        var concurrencyProperties = WalhallaSqlSaveChangesSupport.GetConcurrencyProperties(entityEntry);

        var modifiedProperties = GetUpdateProperties(workItem);
        var columnValues = modifiedProperties
            .Select(property => new StoreColumnValue(GetColumnName(property.Property), property.Value, property.Property))
            .Concat(GetJsonContainerColumnValues(workItem))
            .GroupBy(value => value.ColumnName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        if (columnValues.Length == 0)
        {
            throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.NoOpModifiedEntry,
                $"SaveChanges UPDATE for '{entityEntry.Metadata.Name}' has no modified scalar properties.",
                "Mark at least one scalar property as modified or avoid calling SaveChanges for no-op updates.");
        }

        var setSql = string.Join(", ", columnValues.Select(value =>
            $"{value.ColumnName} = {WalhallaSqlEfCoreSqlRenderer.FormatSqlLiteral(value.Value, value.Property)}"));
        var whereSql = WalhallaSqlSaveChangesSupport.BuildKeyAndConcurrencyPredicate(
            entityEntry,
            collectionName,
            keyProperties,
            concurrencyProperties,
            property => GetRootCurrentValue(workItem, property),
            property => GetRootOriginalOrCurrentValue(workItem, property),
            "UPDATE");

        return new SaveChangesCommand(
            $"UPDATE {collectionName} SET {setSql} WHERE {whereSql}",
            RequiresAffectedRow: true,
            KeyDescription: BuildPrimaryKeyDescription(
                entityEntry,
                keyProperties,
                property => GetRootCurrentValue(workItem, property)));
    }

    private SaveChangesCommand BuildDeleteCommand(SaveChangesWorkItem workItem)
    {
        var entityEntry = workItem.RootEntityEntry;
        var collectionName = ResolveCollectionName(entityEntry.Metadata);
        var keyProperties = GetPrimaryKeyProperties(entityEntry);
        var concurrencyProperties = WalhallaSqlSaveChangesSupport.GetConcurrencyProperties(entityEntry);
        var whereSql = WalhallaSqlSaveChangesSupport.BuildKeyAndConcurrencyPredicate(
            entityEntry,
            collectionName,
            keyProperties,
            concurrencyProperties,
            property => GetRootOriginalOrCurrentValue(workItem, property),
            property => GetRootOriginalOrCurrentValue(workItem, property),
            "DELETE");

        return new SaveChangesCommand(
            $"DELETE FROM {collectionName} WHERE {whereSql}",
            RequiresAffectedRow: true,
            KeyDescription: BuildPrimaryKeyDescription(
                entityEntry,
                keyProperties,
                property => GetRootOriginalOrCurrentValue(workItem, property)));
    }

    private IReadOnlyList<GeneratedKeyChange> MaterializeGeneratedKeys(IEnumerable<IUpdateEntry> addedEntries)
    {
        var addedEntryArray = addedEntries.ToArray();
        var nextGeneratedNumericKeys = new Dictionary<GeneratedKeySequenceKey, object?>();
        var generatedKeyChanges = new List<GeneratedKeyChange>();

        foreach (var entry in addedEntryArray)
        {
            var primaryKey = entry.EntityType.FindPrimaryKey();
            if (primaryKey == null || primaryKey.Properties.Count != 1)
                continue;

            var keyProperty = primaryKey.Properties[0];
            if (keyProperty.ValueGenerated != ValueGenerated.OnAdd)
                continue;

            var entityEntry = entry.ToEntityEntry();
            var clrValue = keyProperty.GetGetter().GetClrValue(entityEntry.Entity);
            if (!KeyEntryIsTemporaryOrDefault(entityEntry, keyProperty, clrValue))
                continue;

            var keyEntry = entityEntry.Property(keyProperty.Name);
            var previousValue = keyEntry.CurrentValue;
            keyEntry.CurrentValue = GenerateKeyValue(entityEntry, keyProperty, addedEntryArray, nextGeneratedNumericKeys);
            keyEntry.IsTemporary = false;

            if (!Equals(previousValue, keyEntry.CurrentValue))
            {
                generatedKeyChanges.Add(new GeneratedKeyChange(
                    entityEntry,
                    keyProperty,
                    previousValue,
                    keyEntry.CurrentValue));
            }
        }

        return generatedKeyChanges;

        static bool KeyEntryIsTemporaryOrDefault(EntityEntry entityEntry, IProperty keyProperty, object? clrValue)
        {
            var keyEntry = entityEntry.Property(keyProperty.Name);
            if (keyEntry.IsTemporary)
                return true;

            return IsNullOrDefault(clrValue, keyProperty.ClrType);
        }
    }

    private object GenerateKeyValue(
        EntityEntry entityEntry,
        IProperty keyProperty,
        IReadOnlyList<IUpdateEntry> addedEntries,
        IDictionary<GeneratedKeySequenceKey, object?> nextGeneratedNumericKeys)
    {
        var clrType = Nullable.GetUnderlyingType(keyProperty.ClrType) ?? keyProperty.ClrType;
        if (clrType == typeof(Guid))
            return Guid.NewGuid();

        var keyEntityType = ResolveStoreEntityType(entityEntry.Metadata, keyProperty);
        var collectionName = ResolveCollectionName(keyEntityType);
        var columnName = GetColumnName(keyProperty);
        var sequenceKey = new GeneratedKeySequenceKey(collectionName, columnName, clrType, keyEntityType.Name);

        if (!nextGeneratedNumericKeys.TryGetValue(sequenceKey, out var currentMax))
        {
            var result = ExecuteSqlInternal($"SELECT {columnName} FROM {collectionName} ORDER BY {columnName} DESC LIMIT 1", transaction: null);
            currentMax = result.Rows?.FirstOrDefault()?.GetValueOrDefault(columnName);

            foreach (var explicitValue in EnumerateExplicitBatchKeyValues(addedEntries, sequenceKey))
            {
                if (currentMax == null || CompareNumericKeyValues(explicitValue, currentMax, clrType) > 0)
                    currentMax = explicitValue;
            }
        }

        var nextValue = GenerateNextNumericKey(currentMax, clrType, entityEntry.Metadata.Name);
        nextGeneratedNumericKeys[sequenceKey] = nextValue;
        return nextValue;
    }

    private IEnumerable<object> EnumerateExplicitBatchKeyValues(
        IReadOnlyList<IUpdateEntry> addedEntries,
        GeneratedKeySequenceKey sequenceKey)
    {
        foreach (var entry in addedEntries)
        {
            var primaryKey = entry.EntityType.FindPrimaryKey();
            if (primaryKey == null || primaryKey.Properties.Count != 1)
                continue;

            var keyProperty = primaryKey.Properties[0];
            if (keyProperty.ValueGenerated != ValueGenerated.OnAdd)
                continue;

            var entityEntry = entry.ToEntityEntry();
            var keyEntityType = ResolveStoreEntityType(entityEntry.Metadata, keyProperty);
            if (!string.Equals(ResolveCollectionName(keyEntityType), sequenceKey.CollectionName, StringComparison.Ordinal)
                || !string.Equals(GetColumnName(keyProperty), sequenceKey.ColumnName, StringComparison.Ordinal))
            {
                continue;
            }

            var clrValue = keyProperty.GetGetter().GetClrValue(entityEntry.Entity);
            var keyEntry = entityEntry.Property(keyProperty.Name);
            if (keyEntry.IsTemporary || IsNullOrDefault(clrValue, keyProperty.ClrType))
                continue;

            yield return clrValue!;
        }
    }

    private static int CompareNumericKeyValues(object left, object right, Type clrType)
    {
        if (clrType == typeof(int))
            return Convert.ToInt32(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToInt32(right, CultureInfo.InvariantCulture));
        if (clrType == typeof(long))
            return Convert.ToInt64(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToInt64(right, CultureInfo.InvariantCulture));
        if (clrType == typeof(short))
            return Convert.ToInt16(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToInt16(right, CultureInfo.InvariantCulture));
        if (clrType == typeof(uint))
            return Convert.ToUInt32(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToUInt32(right, CultureInfo.InvariantCulture));
        if (clrType == typeof(ulong))
            return Convert.ToUInt64(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToUInt64(right, CultureInfo.InvariantCulture));
        if (clrType == typeof(ushort))
            return Convert.ToUInt16(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToUInt16(right, CultureInfo.InvariantCulture));
        if (clrType == typeof(byte))
            return Convert.ToByte(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToByte(right, CultureInfo.InvariantCulture));

        throw new InvalidOperationException($"Unsupported numeric key comparison type '{clrType.Name}'.");
    }

    private static IEntityType ResolveStoreEntityType(IEntityType fallbackEntityType, IProperty property)
    {
        return property.DeclaringType as IEntityType ?? fallbackEntityType;
    }

    private static void PropagateGeneratedForeignKeys(
        IReadOnlyList<EntityEntry> trackedEntries,
        IReadOnlyList<GeneratedKeyChange> generatedKeyChanges)
    {
        if (generatedKeyChanges.Count == 0)
            return;

        foreach (var keyChange in generatedKeyChanges)
        {
            foreach (var dependentEntry in trackedEntries)
            {
                if (ReferenceEquals(dependentEntry.Entity, keyChange.PrincipalEntry.Entity))
                    continue;

                foreach (var foreignKey in dependentEntry.Metadata.GetForeignKeys())
                {
                    if (foreignKey.IsOwnership || foreignKey.Properties.Count != 1 || foreignKey.PrincipalKey.Properties.Count != 1)
                        continue;

                    if (!MatchesEntityTypeOrDerived(keyChange.PrincipalEntry.Metadata, foreignKey.PrincipalEntityType)
                        || !ReferenceEquals(foreignKey.PrincipalKey.Properties[0], keyChange.KeyProperty))
                    {
                        continue;
                    }

                    var foreignKeyEntry = dependentEntry.Property(foreignKey.Properties[0].Name);
                    var matchesPrincipal = foreignKey.DependentToPrincipal != null
                        && ReferenceEquals(dependentEntry.Reference(foreignKey.DependentToPrincipal.Name).CurrentValue, keyChange.PrincipalEntry.Entity);
                    var matchesTemporaryKey = Equals(foreignKeyEntry.CurrentValue, keyChange.PreviousValue)
                        || Equals(foreignKeyEntry.OriginalValue, keyChange.PreviousValue);

                    if (!matchesPrincipal && !matchesTemporaryKey)
                        continue;

                    foreignKeyEntry.CurrentValue = keyChange.CurrentValue;
                }
            }
        }
    }

    private readonly record struct GeneratedKeySequenceKey(string CollectionName, string ColumnName, Type ClrType, string EntityName);

    private sealed record GeneratedKeyChange(EntityEntry PrincipalEntry, IProperty KeyProperty, object? PreviousValue, object? CurrentValue);

    private static object GenerateNextNumericKey(object? currentMax, Type clrType, string entityName)
    {
        if (clrType == typeof(int))
            return checked((currentMax == null ? 0 : Convert.ToInt32(currentMax, CultureInfo.InvariantCulture)) + 1);
        if (clrType == typeof(long))
            return checked((currentMax == null ? 0L : Convert.ToInt64(currentMax, CultureInfo.InvariantCulture)) + 1L);
        if (clrType == typeof(short))
            return checked((short)((currentMax == null ? (short)0 : Convert.ToInt16(currentMax, CultureInfo.InvariantCulture)) + 1));
        if (clrType == typeof(uint))
            return checked((currentMax == null ? 0u : Convert.ToUInt32(currentMax, CultureInfo.InvariantCulture)) + 1u);
        if (clrType == typeof(ulong))
            return checked((currentMax == null ? 0ul : Convert.ToUInt64(currentMax, CultureInfo.InvariantCulture)) + 1ul);
        if (clrType == typeof(ushort))
            return checked((ushort)((currentMax == null ? (ushort)0 : Convert.ToUInt16(currentMax, CultureInfo.InvariantCulture)) + 1));
        if (clrType == typeof(byte))
            return checked((byte)((currentMax == null ? (byte)0 : Convert.ToByte(currentMax, CultureInfo.InvariantCulture)) + 1));

        throw EfSaveChangesGuardrail.NotSupportedWithHint(
            EfSaveChangesGuardrail.Codes.UnsupportedKeyGeneration,
            $"SaveChanges INSERT for '{entityName}' does not support generated keys of CLR type '{clrType.Name}'.",
            "Use Guid or a numeric single-column primary key for ValueGeneratedOnAdd in the current provider implementation.");
    }

    private static IReadOnlyList<IUpdateEntry> GetConcurrencyEntries(SaveChangesWorkItem workItem)
    {
        var entries = new List<IUpdateEntry>();

        if (workItem.RootUpdateEntry != null)
            entries.Add(workItem.RootUpdateEntry);

        foreach (var (updateEntry, _) in workItem.OwnedEntries)
        {
            if (!entries.Any(existing => ReferenceEquals(existing, updateEntry)))
                entries.Add(updateEntry);
        }

        return entries;
    }

    private static DbUpdateConcurrencyException CreateOptimisticConcurrencyException(SaveChangesWorkItem workItem, SaveChangesCommand command)
    {
        var message =
            $"{EfSaveChangesGuardrail.Prefix} [{EfSaveChangesGuardrail.Codes.ConcurrencyNoRowsAffected}] " +
            $"{workItem.RootEntityEntry.State} on '{workItem.RootEntityEntry.Metadata.Name}' expected at least 1 affected row but got 0. " +
            $"Key: {command.KeyDescription ?? "<unknown>"}.";

        return new DbUpdateConcurrencyException(message, GetConcurrencyEntries(workItem));
    }

    private void ValidateNoDuplicateKey(SaveChangesWorkItem workItem, DbTransaction? transaction)
    {
        var entityEntry = workItem.RootEntityEntry;
        var collectionName = ResolveCollectionName(entityEntry.Metadata);
        var keyProperties = GetPrimaryKeyProperties(entityEntry);

        var filters = new List<(string ColumnName, object? Value, IProperty? Property)>(keyProperties.Count);
        foreach (var keyProperty in keyProperties)
        {
            var keyValue = GetRootCurrentValue(workItem, keyProperty);
            if (keyValue == null)
                continue;
            filters.Add((GetColumnName(keyProperty), keyValue, keyProperty));
        }

        if (filters.Count == 0)
            return;

        var whereSql = WalhallaSqlEfCoreSqlRenderer.RenderEqualityWhereClause(collectionName, filters);
        if (string.IsNullOrWhiteSpace(whereSql))
            return;

        var checkSql = $"SELECT COUNT(*) FROM {collectionName} WHERE {whereSql}";
        var checkResult = ExecuteSqlInternal(checkSql, transaction);

        var count = checkResult.Rows?.FirstOrDefault()?.Values.FirstOrDefault();
        if (count is long l && l > 0 || count is int i && i > 0)
        {
            throw new DbUpdateException(
                $"Cannot insert duplicate key in object '{collectionName}'. The duplicate key value is ({BuildPrimaryKeyDescription(entityEntry, keyProperties, property => GetRootCurrentValue(workItem, property))}).",
                GetConcurrencyEntries(workItem));
        }
    }

    private void ValidateSaveChangesEntries((IUpdateEntry UpdateEntry, EntityEntry EntityEntry)[] entries)
    {
        var updateLogger = _context.GetService<IDiagnosticsLogger<DbLoggerCategory.Update>>();
        var sensitiveLoggingEnabled = (_context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?
            .IsSensitiveDataLoggingEnabled)
            == true;

        foreach (var pair in entries)
        {
            var entityEntry = pair.EntityEntry;
            GetPrimaryKeyProperties(entityEntry);
            WalhallaSqlSaveChangesSupport.ValidatePropertyConstraints(entityEntry);

            if (ShouldWarnForOptionalDependentWithAllNullProperties(pair))
            {
                if (sensitiveLoggingEnabled)
                    updateLogger.OptionalDependentWithAllNullPropertiesWarningSensitive(pair.UpdateEntry);
                else
                    updateLogger.OptionalDependentWithAllNullPropertiesWarning(pair.UpdateEntry);
            }

            var hasChangedReferenceGraph = entityEntry.References.Any(reference =>
            {
                var targetEntry = reference.TargetEntry;
                return targetEntry != null
                    && !IsOwnershipReference(reference)
                    && targetEntry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;
            });

            if (hasChangedReferenceGraph)
                continue;
        }
    }

    private static bool ShouldWarnForOptionalDependentWithAllNullProperties((IUpdateEntry UpdateEntry, EntityEntry EntityEntry) pair)
    {
        if (pair.EntityEntry.State is not (EntityState.Added or EntityState.Modified))
            return false;

        var rowInternalForeignKeys = pair.EntityEntry.Metadata.GetForeignKeys()
            .Where(foreignKey => foreignKey.IsUnique)
            .Where(foreignKey => !foreignKey.IsRequiredDependent)
            .Where(foreignKey => IsSharedTableIdentifyingForeignKey(pair.EntityEntry.Metadata, foreignKey))
            .ToArray();

        if (rowInternalForeignKeys.Length == 0)
            return false;

        var principalColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var foreignKey in rowInternalForeignKeys)
        {
            foreach (var principalProperty in foreignKey.PrincipalEntityType.GetFlattenedProperties())
            {
                var columnName = GetColumnName(principalProperty);
                if (!string.IsNullOrWhiteSpace(columnName))
                    principalColumns.Add(columnName);
            }
        }

        foreach (var property in pair.EntityEntry.Metadata.GetFlattenedProperties())
        {
            if (property.IsPrimaryKey())
                continue;

            var columnName = GetColumnName(property);
            if (string.IsNullOrWhiteSpace(columnName) || principalColumns.Contains(columnName))
                continue;

            if (pair.UpdateEntry.GetCurrentValue(property) != null)
                return false;
        }

        return true;
    }

    private static IReadOnlyList<IProperty> GetPrimaryKeyProperties(EntityEntry entry)
    {
        var primaryKey = entry.Metadata.FindPrimaryKey()
            ?? throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.SingleColumnPrimaryKey,
                $"SaveChanges for '{entry.Metadata.Name}' requires a defined primary key.",
                "Define a primary key in the EF model for this entity.");

        return primaryKey.Properties;
    }

    private static string BuildPrimaryKeyPredicate(
        EntityEntry entry,
        string collectionName,
        IReadOnlyList<IProperty> keyProperties,
        Func<IProperty, object?> valueAccessor,
        string operation)
    {
        var filters = new List<(string ColumnName, object? Value, IProperty? Property)>(keyProperties.Count);

        foreach (var keyProperty in keyProperties)
        {
            var keyValue = valueAccessor(keyProperty);
            if (keyValue == null)
            {
                throw EfSaveChangesGuardrail.NotSupportedWithHint(
                    EfSaveChangesGuardrail.Codes.NonNullPrimaryKey,
                    $"SaveChanges {operation} for '{entry.Metadata.Name}' requires non-null primary key values.",
                    "Ensure every primary key property is set before calling SaveChanges.");
            }

            filters.Add((GetColumnName(keyProperty), keyValue, keyProperty));
        }

        return WalhallaSqlEfCoreSqlRenderer.RenderEqualityWhereClause(collectionName, filters)
            ?? throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.SingleColumnPrimaryKey,
                $"SaveChanges {operation} for '{entry.Metadata.Name}' could not translate the primary key predicate.",
                "Use primary key types that can be converted into WalhallaSql scalar values.");
    }

    private static string BuildPrimaryKeyDescription(
        EntityEntry entry,
        IReadOnlyList<IProperty> keyProperties,
        Func<IProperty, object?> valueAccessor)
    {
        return string.Join(
            ", ",
            keyProperties.Select(property =>
            {
                var value = valueAccessor(property);
                return $"{property.Name}={WalhallaSqlEfCoreSqlRenderer.FormatSqlLiteral(value, property)}";
            }));
    }

    private static string FormatSqlLiteral(object? value)
    {
        if (value == null)
            return "NULL";

        return value switch
        {
            bool b => b ? "1" : "0",
            string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
            DateOnly dateOnly => $"'{dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
            TimeOnly timeOnly => $"'{timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'",
            DateTime dt => $"'{dt.ToString("O", CultureInfo.InvariantCulture)}'",
            DateTimeOffset dto => $"'{dto.ToString("O", CultureInfo.InvariantCulture)}'",
            _ when IsNumeric(value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''", StringComparison.Ordinal)}'"
        };
    }

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

    private static string GetColumnName(IProperty property, StoreObjectIdentifier storeObject)
    {
        var relationalName = property.GetColumnName(storeObject);
        if (!string.IsNullOrEmpty(relationalName))
            return relationalName;

        return GetColumnName(property);
    }

    private static object? ToProviderValue(object? clrValue, IProperty property)
    {
        var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;
        if (converter != null)
            return converter.ConvertToProvider(clrValue);

        if (clrValue != null)
        {
            var clrType = clrValue.GetType();
            if (clrType.IsEnum)
                return Convert.ChangeType(clrValue, Enum.GetUnderlyingType(clrType), CultureInfo.InvariantCulture);
        }

        return clrValue;
    }

    private static bool IsNumeric(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }


    private static bool IsNullOrDefault(object? value, Type clrType)
    {
        if (value == null)
            return true;

        var targetType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (!targetType.IsValueType)
            return false;

        var defaultValue = Activator.CreateInstance(targetType);
        return Equals(value, defaultValue);
    }

    private IReadOnlyList<SaveChangesWorkItem> BuildSaveChangesWorkItems((IUpdateEntry UpdateEntry, EntityEntry EntityEntry)[] entries)
    {
        var trackedEntries = _context.ChangeTracker.Entries().ToArray();
        var changedEntries = entries
            .Where(pair => pair.EntityEntry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        changedEntries = IncludeMissingDeletedRootReplacements(changedEntries, trackedEntries);

        var byRoot = new Dictionary<object, SaveChangesWorkItemBuilder>();
        foreach (var pair in changedEntries)
        {
            var rootEntry = GetAggregateRootEntry(pair.EntityEntry, trackedEntries);
            if (!byRoot.TryGetValue(rootEntry.Entity, out var builder))
            {
                var rootPair = changedEntries.FirstOrDefault(item => ReferenceEquals(item.EntityEntry.Entity, rootEntry.Entity));
                builder = new SaveChangesWorkItemBuilder(rootEntry, rootPair.UpdateEntry);
                byRoot[rootEntry.Entity] = builder;
            }

            builder.Entries.Add(pair);
        }

        var workItems = byRoot.Values
            .Select(builder => CreateWorkItem(builder, trackedEntries))
            .Where(item => item.EffectiveState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        ValidateOwnedTypeMappings(workItems);
        return OrderWorkItems(workItems);
    }

    private (IUpdateEntry UpdateEntry, EntityEntry EntityEntry)[] IncludeMissingDeletedRootReplacements(
        (IUpdateEntry UpdateEntry, EntityEntry EntityEntry)[] changedEntries,
        IReadOnlyList<EntityEntry> trackedEntries)
    {
        if (!changedEntries.Any(pair => pair.EntityEntry.State == EntityState.Added))
            return changedEntries;

        var supplementedEntries = changedEntries.ToList();

        foreach (var addedEntry in changedEntries.Where(pair => pair.EntityEntry.State == EntityState.Added))
        {
            foreach (var deletedRoot in trackedEntries)
            {
                if (deletedRoot.State != EntityState.Deleted)
                    continue;

                if (supplementedEntries.Any(pair => ReferenceEquals(pair.EntityEntry, deletedRoot)))
                    continue;

                if (!ReferenceEquals(GetAggregateRootEntry(deletedRoot, trackedEntries), deletedRoot))
                    continue;

                if (!SharesStoreIdentity(deletedRoot, addedEntry.EntityEntry))
                    continue;

                supplementedEntries.Add((GetUpdateEntry(deletedRoot), deletedRoot));
            }
        }

        return supplementedEntries.ToArray();
    }

    private EntityEntry GetAggregateRootEntry(EntityEntry entry, IReadOnlyList<EntityEntry> knownEntries)
    {
        var current = entry;
        while (true)
        {
            var ownership = current.Metadata.FindOwnership();
            if (ownership == null)
            {
                var sharedTablePrincipalEntry = ResolveSharedTablePrincipalEntry(current, knownEntries);
                if (sharedTablePrincipalEntry == null)
                    return current;

                current = sharedTablePrincipalEntry;
                continue;
            }

            var ownerEntry = current.References
                .FirstOrDefault(IsOwnershipReference)
                ?.TargetEntry;

            if (ownerEntry == null)
            {
                ownerEntry = knownEntries.FirstOrDefault(candidate =>
                    candidate.References.Any(reference =>
                        IsOwnershipReference(reference) &&
                        ReferenceEquals(reference.TargetEntry, current)));
            }

            if (ownerEntry == null)
            {
                ownerEntry = knownEntries.FirstOrDefault(candidate =>
                    candidate.References.Any(reference =>
                        IsOwnershipReference(reference) &&
                        ReferenceEquals(reference.CurrentValue, current.Entity)));
            }

            if (ownerEntry == null)
            {
                ownerEntry = knownEntries.FirstOrDefault(candidate =>
                    candidate.Navigations.Any(navigation =>
                        navigation.Metadata.TargetEntityType == current.Metadata &&
                        ReferenceEquals(navigation.CurrentValue, current.Entity)));
            }

            if (ownerEntry == null)
            {
                ownerEntry = knownEntries.FirstOrDefault(candidate =>
                    MatchesEntityTypeOrDerived(candidate.Metadata, ownership.PrincipalEntityType) &&
                    OwnershipMatches(candidate, current, ownership));
            }

            if (ownerEntry == null)
            {
                throw EfSaveChangesGuardrail.NotSupportedWithHint(
                    EfSaveChangesGuardrail.Codes.OwnedTypes,
                    $"Owned entity type '{current.Metadata.Name}' could not resolve its owner for SaveChanges.",
                    "Track the owner entity together with the owned instance in the same DbContext.");
            }

            current = ownerEntry;
        }
    }

    private static EntityEntry? ResolveSharedTablePrincipalEntry(EntityEntry entry, IReadOnlyList<EntityEntry> knownEntries)
    {
        foreach (var foreignKey in entry.Metadata.GetForeignKeys())
        {
            if (foreignKey.IsOwnership || !foreignKey.IsUnique)
                continue;

            if (!IsSharedTableIdentifyingForeignKey(entry.Metadata, foreignKey))
                continue;

            var principalEntry = knownEntries.FirstOrDefault(candidate =>
                MatchesEntityTypeOrDerived(candidate.Metadata, foreignKey.PrincipalEntityType)
                && !ReferenceEquals(candidate, entry)
                && ForeignKeyMatches(entry, candidate, foreignKey, useOriginalValues: false));

            if (principalEntry != null)
                return principalEntry;
        }

        return null;
    }

    private static bool IsSharedTableIdentifyingForeignKey(IEntityType dependentType, IForeignKey foreignKey)
    {
        if (!foreignKey.Properties.All(property => property.IsPrimaryKey()))
            return false;

        var dependentTable = dependentType.GetTableName();
        var principalTable = foreignKey.PrincipalEntityType.GetTableName();
        if (string.IsNullOrWhiteSpace(dependentTable)
            || string.IsNullOrWhiteSpace(principalTable)
            || !string.Equals(dependentTable, principalTable, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dependentSchema = dependentType.GetSchema();
        var principalSchema = foreignKey.PrincipalEntityType.GetSchema();
        return string.Equals(dependentSchema, principalSchema, StringComparison.OrdinalIgnoreCase);
    }

    private static bool OwnershipMatches(EntityEntry ownerEntry, EntityEntry ownedEntry, IForeignKey ownership)
    {
        for (var i = 0; i < ownership.Properties.Count; i++)
        {
            if (!PropertyValuesMatch(
                    ownedEntry,
                    ownership.Properties[i],
                    ownerEntry,
                    ownership.PrincipalKey.Properties[i],
                    useOriginalValues: false))
                return false;
        }

        return true;
    }

    private SaveChangesWorkItem CreateWorkItem(SaveChangesWorkItemBuilder builder, IReadOnlyList<EntityEntry> trackedEntries)
    {
        var ownedEntries = builder.Entries
            .Where(entry => !ReferenceEquals(entry.EntityEntry, builder.RootEntityEntry))
            .Concat(ResolveUnchangedSharedTableDependents(builder.RootEntityEntry, builder.Entries, trackedEntries))
            .ToArray();

        var effectiveState = builder.RootEntityEntry.State switch
        {
            EntityState.Added => EntityState.Added,
            EntityState.Deleted => EntityState.Deleted,
            EntityState.Modified => EntityState.Modified,
            _ when ownedEntries.Any(entry => entry.EntityEntry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) => EntityState.Modified,
            _ => EntityState.Unchanged
        };

        return new SaveChangesWorkItem(builder.RootUpdateEntry, builder.RootEntityEntry, ownedEntries, effectiveState);
    }

    private static IEnumerable<(IUpdateEntry UpdateEntry, EntityEntry EntityEntry)> ResolveUnchangedSharedTableDependents(
        EntityEntry rootEntry,
        IReadOnlyList<(IUpdateEntry UpdateEntry, EntityEntry EntityEntry)> existingEntries,
        IReadOnlyList<EntityEntry> trackedEntries)
    {
        foreach (var candidate in trackedEntries)
        {
            if (ReferenceEquals(candidate, rootEntry))
                continue;

            if (candidate.State != EntityState.Unchanged)
                continue;

            if (existingEntries.Any(entry => ReferenceEquals(entry.EntityEntry, candidate)))
                continue;

            foreach (var foreignKey in candidate.Metadata.GetForeignKeys())
            {
                if (foreignKey.IsOwnership || !foreignKey.IsUnique)
                    continue;

                if (!IsSharedTableIdentifyingForeignKey(candidate.Metadata, foreignKey))
                    continue;

                if (!MatchesEntityTypeOrDerived(rootEntry.Metadata, foreignKey.PrincipalEntityType))
                    continue;

                if (!ForeignKeyMatches(candidate, rootEntry, foreignKey, useOriginalValues: false))
                    continue;

                yield return (GetUpdateEntry(candidate), candidate);
                break;
            }
        }
    }

#pragma warning disable EF1001
    private static IUpdateEntry GetUpdateEntry(EntityEntry entry)
        => (IUpdateEntry)entry.GetInfrastructure();
#pragma warning restore EF1001

    private void ValidateOwnedTypeMappings(IReadOnlyList<SaveChangesWorkItem> workItems)
    {
        foreach (var workItem in workItems)
        {
            var rootCollection = ResolveCollectionName(workItem.RootEntityEntry.Metadata);
            foreach (var ownedEntry in workItem.OwnedEntries)
            {
                var mappedTable = ownedEntry.EntityEntry.Metadata.GetTableName();
                if (!string.IsNullOrEmpty(mappedTable)
                    && workItem.EffectiveState != EntityState.Added
                    && !string.Equals(rootCollection, mappedTable, StringComparison.OrdinalIgnoreCase))
                {
                    throw EfSaveChangesGuardrail.NotSupportedWithHint(
                        EfSaveChangesGuardrail.Codes.OwnedTypes,
                        $"Owned entity type '{ownedEntry.EntityEntry.Metadata.Name}' mapped to a different table is not supported in SaveChanges MVP.",
                        "Keep owned types table-split with their owner or persist them outside the current SaveChanges path.");
                }
            }
        }
    }

    private static IReadOnlyList<StoreObjectIdentifier> GetInsertStoreObjects(SaveChangesWorkItem workItem)
    {
        var storeObjects = new List<StoreObjectIdentifier>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hierarchy = new Stack<IEntityType>();
        for (var current = ResolveDiscriminatorEntityType(workItem.RootEntityEntry); current != null; current = current.BaseType)
            hierarchy.Push(current);

        while (hierarchy.Count > 0)
            TryAddStoreObject(hierarchy.Pop());

        foreach (var ownedEntry in workItem.OwnedEntries)
            TryAddStoreObject(ResolveDiscriminatorEntityType(ownedEntry.EntityEntry));

        return storeObjects;

        void TryAddStoreObject(IEntityType entityType)
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrWhiteSpace(tableName))
                return;

            var schema = entityType.GetSchema();
            var key = $"{schema ?? string.Empty}|{tableName}";
            if (!used.Add(key))
                return;

            storeObjects.Add(StoreObjectIdentifier.Table(tableName, schema));
        }
    }

    private string ResolveInsertCollectionName(SaveChangesWorkItem workItem, StoreObjectIdentifier storeObject)
    {
        for (var current = ResolveDiscriminatorEntityType(workItem.RootEntityEntry); current != null; current = current.BaseType)
        {
            if (MapsDirectlyToStoreObject(current, storeObject))
                return ResolveCollectionName(current);
        }

        foreach (var ownedEntry in workItem.OwnedEntries)
        {
            var entityType = ResolveDiscriminatorEntityType(ownedEntry.EntityEntry);
            if (MapsDirectlyToStoreObject(entityType, storeObject))
                return ResolveCollectionName(entityType);
        }

        return WalhallaSqlStoreObjectNameSanitizer.Sanitize(storeObject.Name);
    }

    private IReadOnlyList<SaveChangesWorkItem> OrderWorkItems(IReadOnlyList<SaveChangesWorkItem> workItems)
    {
        var dependencies = workItems.ToDictionary(item => item, _ => new HashSet<SaveChangesWorkItem>());
        var dependents = workItems.ToDictionary(item => item, _ => new HashSet<SaveChangesWorkItem>());

        foreach (var item in workItems)
        {
            var entriesToCheck = new List<EntityEntry> { item.RootEntityEntry };
            entriesToCheck.AddRange(item.OwnedEntries.Select(e => e.EntityEntry));

            foreach (var entry in entriesToCheck)
            {
                foreach (var foreignKey in entry.Metadata.GetForeignKeys())
                {
                    if (foreignKey.IsOwnership)
                        continue;

                    var targetItem = workItems.FirstOrDefault(candidate =>
                        MatchesEntityTypeOrDerived(candidate.RootEntityEntry.Metadata, foreignKey.PrincipalEntityType) &&
                        ForeignKeyMatches(entry, candidate.RootEntityEntry, foreignKey, useOriginalValues: false));
                    if (targetItem == null)
                        continue;

                    if (ReferenceEquals(item, targetItem))
                        continue;

                    TryAddRelationshipDependency(item, entry, targetItem, foreignKey, dependencies, dependents);
                }
            }
        }

        var ordered = new List<SaveChangesWorkItem>(workItems.Count);
        var ready = workItems
            .Where(item => dependencies[item].Count == 0)
            .OrderBy(GetStateOrdering)
            .ToList();

        while (ready.Count > 0)
        {
            var current = ready[0];
            ready.RemoveAt(0);
            ordered.Add(current);

            foreach (var dependent in dependents[current])
            {
                dependencies[dependent].Remove(current);
                if (dependencies[dependent].Count == 0)
                {
                    ready.Add(dependent);
                    ready.Sort((left, right) => GetStateOrdering(left).CompareTo(GetStateOrdering(right)));
                }
            }
        }

        if (ordered.Count != workItems.Count)
        {
            throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.ComplexGraph,
                "Entity graph persistence detected a cycle, which is not supported in SaveChanges MVP.",
                "Persist cyclic graphs in separate SaveChanges calls.");
        }

        return ordered;
    }

    private static int GetStateOrdering(SaveChangesWorkItem item)
    {
        return item.EffectiveState switch
        {
            EntityState.Deleted => 0,
            EntityState.Modified => 1,
            EntityState.Added => 2,
            _ => 3
        };
    }

    private static void TryAddRelationshipDependency(
        SaveChangesWorkItem dependentItem,
        EntityEntry dependentEntry,
        SaveChangesWorkItem principalItem,
        IForeignKey foreignKey,
        IReadOnlyDictionary<SaveChangesWorkItem, HashSet<SaveChangesWorkItem>> dependencies,
        IReadOnlyDictionary<SaveChangesWorkItem, HashSet<SaveChangesWorkItem>> dependents)
    {
        var currentMatch = ForeignKeyMatches(dependentEntry, principalItem.RootEntityEntry, foreignKey, useOriginalValues: false);
        var originalMatch = ForeignKeyMatches(dependentEntry, principalItem.RootEntityEntry, foreignKey, useOriginalValues: true);

        if (currentMatch && RequiresPrincipalBeforeDependent(dependentItem.EffectiveState, principalItem.EffectiveState))
        {
            AddDependency(dependentItem, principalItem, dependencies, dependents);
            return;
        }

        if (originalMatch && RequiresDependentBeforePrincipal(dependentItem.EffectiveState, principalItem.EffectiveState))
            AddDependency(principalItem, dependentItem, dependencies, dependents);
    }

    private static void AddDependency(
        SaveChangesWorkItem item,
        SaveChangesWorkItem dependency,
        IReadOnlyDictionary<SaveChangesWorkItem, HashSet<SaveChangesWorkItem>> dependencies,
        IReadOnlyDictionary<SaveChangesWorkItem, HashSet<SaveChangesWorkItem>> dependents)
    {
        if (dependencies[item].Add(dependency))
            dependents[dependency].Add(item);
    }

    private static bool RequiresPrincipalBeforeDependent(EntityState dependentState, EntityState principalState)
    {
        return dependentState is EntityState.Added or EntityState.Modified
            && principalState == EntityState.Added;
    }

    private static bool RequiresDependentBeforePrincipal(EntityState dependentState, EntityState principalState)
    {
        return principalState == EntityState.Deleted
            && dependentState is EntityState.Modified or EntityState.Deleted;
    }

    private static bool HasAddedGraphDependencies(IReadOnlyList<SaveChangesWorkItem> workItems)
    {
        foreach (var item in workItems)
        {
            if (item.EffectiveState != EntityState.Added)
                continue;

            var entriesToCheck = new List<EntityEntry> { item.RootEntityEntry };
            entriesToCheck.AddRange(item.OwnedEntries.Select(e => e.EntityEntry));

            foreach (var entry in entriesToCheck)
            {
                foreach (var foreignKey in entry.Metadata.GetForeignKeys())
                {
                    if (foreignKey.IsOwnership)
                        continue;

                    if (workItems.Any(candidate =>
                        !ReferenceEquals(candidate, item)
                        && candidate.EffectiveState == EntityState.Added
                        && MatchesEntityTypeOrDerived(candidate.RootEntityEntry.Metadata, foreignKey.PrincipalEntityType)
                        && ForeignKeyMatches(entry, candidate.RootEntityEntry, foreignKey, useOriginalValues: false)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool HasDeleteGraphDependencies(IReadOnlyList<SaveChangesWorkItem> workItems)
    {
        foreach (var item in workItems)
        {
            if (item.EffectiveState is not EntityState.Modified and not EntityState.Deleted)
                continue;

            var entriesToCheck = new List<EntityEntry> { item.RootEntityEntry };
            entriesToCheck.AddRange(item.OwnedEntries.Select(e => e.EntityEntry));

            foreach (var entry in entriesToCheck)
            {
                foreach (var foreignKey in entry.Metadata.GetForeignKeys())
                {
                    if (foreignKey.IsOwnership)
                        continue;

                    if (workItems.Any(candidate =>
                        !ReferenceEquals(candidate, item)
                        && candidate.EffectiveState == EntityState.Deleted
                        && MatchesEntityTypeOrDerived(candidate.RootEntityEntry.Metadata, foreignKey.PrincipalEntityType)
                        && ForeignKeyMatches(entry, candidate.RootEntityEntry, foreignKey, useOriginalValues: true)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ForeignKeyMatches(EntityEntry dependentEntry, EntityEntry principalEntry, IForeignKey foreignKey, bool useOriginalValues)
    {
        for (var i = 0; i < foreignKey.Properties.Count; i++)
        {
            if (!PropertyValuesMatch(
                    dependentEntry,
                    foreignKey.Properties[i],
                    principalEntry,
                    foreignKey.PrincipalKey.Properties[i],
                    useOriginalValues))
                return false;
        }

        return true;
    }

    private static bool PropertyValuesMatch(
        EntityEntry leftEntry,
        IProperty leftProperty,
        EntityEntry rightEntry,
        IProperty rightProperty,
        bool useOriginalValues)
        => PropertyValuesMatch(
            leftProperty,
            ReadPropertyValue(leftEntry, leftProperty, useOriginalValues),
            rightProperty,
            ReadPropertyValue(rightEntry, rightProperty, useOriginalValues));

    private static object? ReadPropertyValue(EntityEntry entry, IProperty property, bool useOriginalValues)
    {
        var propertyEntry = entry.Property(property.Name);
        return useOriginalValues
            ? propertyEntry.OriginalValue ?? propertyEntry.CurrentValue
            : propertyEntry.CurrentValue;
    }

    private static bool PropertyValuesMatch(IProperty leftProperty, object? leftValue, IProperty rightProperty, object? rightValue)
    {
        var comparer = leftProperty.GetKeyValueComparer()
            ?? rightProperty.GetKeyValueComparer()
            ?? leftProperty.GetValueComparer()
            ?? rightProperty.GetValueComparer();

        if (comparer != null)
            return comparer.Equals(leftValue, rightValue);

        return ValuesEqual(leftValue, rightValue);
    }

    private static bool ValuesEqual(object? leftValue, object? rightValue)
    {
        if (leftValue is null || rightValue is null)
            return leftValue is null && rightValue is null;

        if (leftValue is byte[] leftBytes && rightValue is byte[] rightBytes)
            return leftBytes.AsSpan().SequenceEqual(rightBytes);

        return Equals(leftValue, rightValue);
    }

    private bool SharesStoreIdentity(EntityEntry existingEntry, EntityEntry replacementEntry)
    {
        if (!string.Equals(ResolveCollectionName(existingEntry.Metadata), ResolveCollectionName(replacementEntry.Metadata), StringComparison.OrdinalIgnoreCase))
            return false;

        var existingKey = existingEntry.Metadata.FindPrimaryKey();
        var replacementKey = replacementEntry.Metadata.FindPrimaryKey();
        if (existingKey == null || replacementKey == null || existingKey.Properties.Count != replacementKey.Properties.Count)
            return false;

        var replacementKeyValues = replacementKey.Properties.ToDictionary(
            property => GetColumnName(property),
            property => replacementEntry.Property(property.Name).CurrentValue,
            StringComparer.OrdinalIgnoreCase);

        foreach (var existingProperty in existingKey.Properties)
        {
            var columnName = GetColumnName(existingProperty);
            if (!replacementKeyValues.TryGetValue(columnName, out var replacementValue))
                return false;

            var propertyEntry = existingEntry.Property(existingProperty.Name);
            var existingValue = existingEntry.State == EntityState.Deleted
                ? propertyEntry.OriginalValue ?? propertyEntry.CurrentValue
                : propertyEntry.CurrentValue;

            var replacementProperty = replacementKey.Properties.First(property =>
                string.Equals(GetColumnName(property), columnName, StringComparison.OrdinalIgnoreCase));

            if (!PropertyValuesMatch(existingProperty, existingValue, replacementProperty, replacementValue))
                return false;
        }

        return true;
    }

    private static object? GetRootCurrentValue(SaveChangesWorkItem workItem, IProperty property)
    {
        return workItem.RootUpdateEntry != null
            ? workItem.RootUpdateEntry.GetCurrentValue(property)
            : workItem.RootEntityEntry.Property(property.Name).CurrentValue;
    }

    private static object? GetRootOriginalOrCurrentValue(SaveChangesWorkItem workItem, IProperty property)
    {
        if (workItem.RootUpdateEntry != null)
            return workItem.RootUpdateEntry.GetOriginalValue(property) ?? workItem.RootUpdateEntry.GetCurrentValue(property);

        var propertyEntry = workItem.RootEntityEntry.Property(property.Name);
        return propertyEntry.OriginalValue ?? propertyEntry.CurrentValue;
    }

    private static bool IsRootPropertyModified(SaveChangesWorkItem workItem, IProperty property)
    {
        return workItem.RootUpdateEntry != null
            ? workItem.RootUpdateEntry.IsModified(property)
            : workItem.RootEntityEntry.Property(property.Name).IsModified;
    }

    private static bool MatchesEntityTypeOrDerived(IEntityType candidate, IEntityType target)
    {
        for (var current = candidate; current != null; current = current.BaseType)
        {
            if (ReferenceEquals(current, target))
                return true;
        }

        return false;
    }

    private PropertyValue[] GetInsertProperties(SaveChangesWorkItem workItem, StoreObjectIdentifier storeObject)
    {
        var values = new List<PropertyValue>();

        values.AddRange(GetInsertPropertiesForEntry(workItem.RootEntityEntry, workItem.RootUpdateEntry, storeObject));

        AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(workItem.RootEntityEntry), storeObject);

        foreach (var ownedEntry in workItem.OwnedEntries)
        {
            values.AddRange(GetInsertPropertiesForEntry(ownedEntry.EntityEntry, ownedEntry.UpdateEntry, storeObject));
            AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(ownedEntry.EntityEntry), storeObject);
        }

        return DistinctPropertiesByColumn(values, storeObject);
    }

    private PropertyValue[] GetInsertProperties(
        IReadOnlyList<(EntityEntry EntityEntry, IUpdateEntry? UpdateEntry)> entryGroup,
        StoreObjectIdentifier storeObject)
    {
        var values = new List<PropertyValue>();

        foreach (var entry in entryGroup)
        {
            values.AddRange(GetInsertPropertiesForEntry(entry.EntityEntry, entry.UpdateEntry, storeObject));
            AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(entry.EntityEntry), storeObject);
        }

        return DistinctPropertiesByColumn(values, storeObject);
    }

    private PropertyValue[] GetInsertProperties(SaveChangesWorkItem workItem)
    {
        var values = new List<PropertyValue>();

        values.AddRange(workItem.RootEntityEntry.Metadata.GetFlattenedProperties()
            .Where(property => property.ValueGenerated != ValueGenerated.OnAdd || GetRootCurrentValue(workItem, property) != null)
            .Select(property => new PropertyValue(property, GetRootCurrentValue(workItem, property))));

        AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(workItem.RootEntityEntry));

        foreach (var ownedEntry in workItem.OwnedEntries)
            values.AddRange(GetOwnedInsertProperties(ownedEntry));

        return DistinctPropertiesByColumn(values);
    }

    private IEnumerable<PropertyValue> GetInsertPropertiesForEntry(
        EntityEntry entityEntry,
        IUpdateEntry? updateEntry,
        StoreObjectIdentifier storeObject)
    {
        foreach (var property in entityEntry.Metadata.GetFlattenedProperties())
        {
            if (property.GetColumnName(storeObject) == null)
                continue;

            var currentValue = updateEntry != null
                ? updateEntry.GetCurrentValue(property)
                : entityEntry.Property(property.Name).CurrentValue;

            if (property.ValueGenerated == ValueGenerated.OnAdd && currentValue == null)
                continue;

            yield return new PropertyValue(property, currentValue);
        }
    }

    private IReadOnlyList<IReadOnlyList<(EntityEntry EntityEntry, IUpdateEntry? UpdateEntry)>> BuildInsertEntryGroups(
        SaveChangesWorkItem workItem,
        StoreObjectIdentifier storeObject)
    {
        var mappedEntries = new List<(EntityEntry EntityEntry, IUpdateEntry? UpdateEntry)>();
        if (IsMappedToStoreObject(workItem.RootEntityEntry.Metadata, storeObject))
            mappedEntries.Add((workItem.RootEntityEntry, workItem.RootUpdateEntry));

        foreach (var ownedEntry in workItem.OwnedEntries)
        {
            if (IsMappedToStoreObject(ownedEntry.EntityEntry.Metadata, storeObject))
                mappedEntries.Add((ownedEntry.EntityEntry, ownedEntry.UpdateEntry));
        }

        if (mappedEntries.Count == 0)
            return [];

        if (mappedEntries.Any(entry => ReferenceEquals(entry.EntityEntry, workItem.RootEntityEntry)))
            return [mappedEntries];

        var knownEntries = mappedEntries.Select(entry => entry.EntityEntry).ToArray();
        var groups = new Dictionary<object, List<(EntityEntry EntityEntry, IUpdateEntry? UpdateEntry)>>();

        foreach (var entry in mappedEntries)
        {
            var anchor = ResolveInsertGroupAnchor(entry.EntityEntry, knownEntries, storeObject);
            if (!groups.TryGetValue(anchor.Entity, out var group))
            {
                group = new List<(EntityEntry EntityEntry, IUpdateEntry? UpdateEntry)>();
                groups[anchor.Entity] = group;
            }

            group.Add(entry);
        }

        return groups.Values
            .Select(group => (IReadOnlyList<(EntityEntry EntityEntry, IUpdateEntry? UpdateEntry)>)group
                .OrderBy(entry => GetOwnershipDepth(entry.EntityEntry, knownEntries, storeObject))
                .ToArray())
            .ToArray();
    }

    private static EntityEntry ResolveInsertGroupAnchor(
        EntityEntry entry,
        IReadOnlyList<EntityEntry> knownEntries,
        StoreObjectIdentifier storeObject)
    {
        var current = entry;
        while (true)
        {
            var ownerEntry = TryResolveImmediateOwnershipEntry(current, knownEntries);
            if (ownerEntry == null || !IsMappedToStoreObject(ownerEntry.Metadata, storeObject))
                return current;

            current = ownerEntry;
        }
    }

    private static int GetOwnershipDepth(EntityEntry entry, IReadOnlyList<EntityEntry> knownEntries, StoreObjectIdentifier storeObject)
    {
        var depth = 0;
        var current = entry;
        while (true)
        {
            var ownerEntry = TryResolveImmediateOwnershipEntry(current, knownEntries);
            if (ownerEntry == null || !IsMappedToStoreObject(ownerEntry.Metadata, storeObject))
                return depth;

            depth++;
            current = ownerEntry;
        }
    }

    private static EntityEntry? TryResolveImmediateOwnershipEntry(EntityEntry entry, IReadOnlyList<EntityEntry> knownEntries)
    {
        var ownership = entry.Metadata.FindOwnership();
        if (ownership == null)
            return null;

        var ownerEntry = entry.References
            .FirstOrDefault(IsOwnershipReference)
            ?.TargetEntry;

        if (ownerEntry != null)
            return ownerEntry;

        ownerEntry = knownEntries.FirstOrDefault(candidate =>
            candidate.References.Any(reference =>
                IsOwnershipReference(reference)
                && ReferenceEquals(reference.TargetEntry, entry)));

        if (ownerEntry != null)
            return ownerEntry;

        ownerEntry = knownEntries.FirstOrDefault(candidate =>
            candidate.References.Any(reference =>
                IsOwnershipReference(reference)
                && ReferenceEquals(reference.CurrentValue, entry.Entity)));

        if (ownerEntry != null)
            return ownerEntry;

        return knownEntries.FirstOrDefault(candidate =>
            MatchesEntityTypeOrDerived(candidate.Metadata, ownership.PrincipalEntityType)
            && OwnershipMatches(candidate, entry, ownership));
    }

    private static bool IsMappedToStoreObject(IEntityType entityType, StoreObjectIdentifier storeObject)
    {
        if (!entityType.GetFlattenedProperties().Any(property => property.GetColumnName(storeObject) != null))
            return false;

        var tableMappings = entityType.GetTableMappings();
        return tableMappings.Any(mapping =>
            string.Equals(mapping.Table.Name, storeObject.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(mapping.Table.Schema, storeObject.Schema, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MapsDirectlyToStoreObject(IEntityType entityType, StoreObjectIdentifier storeObject)
        => string.Equals(entityType.GetTableName(), storeObject.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entityType.GetSchema(), storeObject.Schema, StringComparison.OrdinalIgnoreCase);

    private PropertyValue[] GetUpdateProperties(SaveChangesWorkItem workItem)
    {
        var values = new List<PropertyValue>();

        values.AddRange(workItem.RootEntityEntry.Metadata.GetFlattenedProperties()
            .Where(property => IsRootPropertyModified(workItem, property))
            .Where(property => !property.IsPrimaryKey())
            .Select(property => new PropertyValue(property, GetRootCurrentValue(workItem, property))));

        foreach (var ownedEntry in workItem.OwnedEntries)
            values.AddRange(GetOwnedUpdateProperties(ownedEntry));

        return DistinctPropertiesByColumn(values);
    }

    private static IEnumerable<PropertyValue> GetOwnedInsertProperties((IUpdateEntry UpdateEntry, EntityEntry EntityEntry) ownedEntry)
    {
        var values = new List<PropertyValue>();

        foreach (var property in ownedEntry.EntityEntry.Metadata.GetFlattenedProperties())
        {
            if (property.IsPrimaryKey())
                continue;

            values.Add(new PropertyValue(property, ownedEntry.UpdateEntry.GetCurrentValue(property)));
        }

        AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(ownedEntry.EntityEntry));

        foreach (var value in values)
            yield return value;
    }

    private static IEnumerable<PropertyValue> GetOwnedUpdateProperties((IUpdateEntry UpdateEntry, EntityEntry EntityEntry) ownedEntry)
    {
        var values = new List<PropertyValue>();

        foreach (var property in ownedEntry.EntityEntry.Metadata.GetFlattenedProperties())
        {
            if (property.IsPrimaryKey())
                continue;

            if (ownedEntry.EntityEntry.State == EntityState.Deleted)
            {
                values.Add(new PropertyValue(property, null));
                continue;
            }

            if (ownedEntry.EntityEntry.State == EntityState.Added || ownedEntry.UpdateEntry.IsModified(property))
                values.Add(new PropertyValue(property, ownedEntry.UpdateEntry.GetCurrentValue(property)));
        }

        AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(ownedEntry.EntityEntry));

        foreach (var value in values)
            yield return value;
    }

    private IEnumerable<StoreColumnValue> GetJsonContainerColumnValues(
        SaveChangesWorkItem workItem,
        StoreObjectIdentifier? storeObject = null)
    {
        foreach (var complexColumn in GetComplexJsonContainerColumnValues(workItem.RootEntityEntry, storeObject))
            yield return complexColumn;

        foreach (var ownedEntry in workItem.OwnedEntries)
        {
            var entityType = ownedEntry.EntityEntry.Metadata;
            if (!entityType.IsMappedToJson())
                continue;

            var ownership = entityType.FindOwnership();
            if (ownership == null || ownership.PrincipalEntityType.IsMappedToJson())
                continue;

            var containerColumnName = entityType.GetContainerColumnName();
            if (string.IsNullOrEmpty(containerColumnName))
                continue;

            var ownerTableName = ownership.PrincipalEntityType.GetTableName();
            if (storeObject.HasValue)
            {
                if (!string.Equals(storeObject.Value.Name, ownerTableName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(storeObject.Value.Schema, ownership.PrincipalEntityType.GetSchema(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            yield return new StoreColumnValue(
                containerColumnName,
                SerializeJsonContainerValue(entityType, ownedEntry.EntityEntry.Entity),
                Property: null);
        }
    }

    private static IEnumerable<StoreColumnValue> GetComplexJsonContainerColumnValues(
        EntityEntry entityEntry,
        StoreObjectIdentifier? storeObject)
    {
        foreach (var complexProperty in entityEntry.Metadata.GetFlattenedComplexProperties())
        {
            var complexType = complexProperty.ComplexType;
            if (!complexType.IsMappedToJson())
                continue;

            if (complexProperty.DeclaringType.IsMappedToJson())
                continue;

            var containerColumnName = complexType.GetContainerColumnName();
            if (string.IsNullOrEmpty(containerColumnName))
                continue;

            var ownerTableName = entityEntry.Metadata.GetTableName();
            if (storeObject.HasValue)
            {
                if (!string.Equals(storeObject.Value.Name, ownerTableName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(storeObject.Value.Schema, entityEntry.Metadata.GetSchema(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var complexValue = entityEntry.ComplexProperty(complexProperty).CurrentValue;
            yield return new StoreColumnValue(
                containerColumnName,
                JsonSerializer.Serialize(BuildComplexJsonPayload(complexType, complexValue)),
                Property: null);
        }
    }

    private static string SerializeJsonContainerValue(IEntityType entityType, object entity)
    {
        var payload = BuildJsonPayload(entityType, entity);
        return JsonSerializer.Serialize(payload);
    }

    private static object? BuildJsonPayload(IEntityType entityType, object? entity)
    {
        if (entity == null)
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in entityType.GetProperties())
        {
            if (property.IsPrimaryKey() || property.IsForeignKey() || property.IsShadowProperty())
                continue;

            var jsonPropertyName = property.GetJsonPropertyName() ?? property.Name;
            payload[jsonPropertyName] = property.GetGetter().GetClrValue(entity);
        }

        foreach (var navigation in entityType.GetNavigations())
        {
            if (!navigation.ForeignKey.IsOwnership || !navigation.TargetEntityType.IsMappedToJson())
                continue;

            var jsonPropertyName = navigation.TargetEntityType.GetJsonPropertyName() ?? navigation.Name;
            var navigationValue = navigation.PropertyInfo?.GetValue(entity);

            if (navigation.IsCollection)
            {
                var items = new List<object?>();
                if (navigationValue is IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                        items.Add(BuildJsonPayload(navigation.TargetEntityType, item));
                }

                payload[jsonPropertyName] = items;
                continue;
            }

            payload[jsonPropertyName] = BuildJsonPayload(navigation.TargetEntityType, navigationValue);
        }

        return payload;
    }

    private static object? BuildComplexJsonPayload(IComplexType complexType, object? instance)
    {
        if (instance == null)
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in complexType.GetProperties())
        {
            if (property.IsShadowProperty())
                continue;

            var jsonPropertyName = property.GetJsonPropertyName() ?? property.Name;
            payload[jsonPropertyName] = property.GetGetter().GetClrValue(instance);
        }

        foreach (var complexProperty in complexType.GetComplexProperties())
        {
            var jsonPropertyName = complexProperty.ComplexType.GetJsonPropertyName() ?? complexProperty.Name;
            var nestedValue = complexProperty.PropertyInfo?.GetValue(instance);
            payload[jsonPropertyName] = BuildComplexJsonPayload(complexProperty.ComplexType, nestedValue);
        }

        return payload;
    }

    private static PropertyValue[] DistinctPropertiesByColumn(IEnumerable<PropertyValue> values, StoreObjectIdentifier storeObject)
    {
        var byColumn = new Dictionary<string, PropertyValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var columnName = GetColumnName(value.Property, storeObject);
            if (!byColumn.ContainsKey(columnName))
                byColumn[columnName] = value;
        }

        return byColumn.Values.ToArray();
    }

    private static PropertyValue[] DistinctPropertiesByColumn(IEnumerable<PropertyValue> values)
    {
        var byColumn = new Dictionary<string, PropertyValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var columnName = GetColumnName(value.Property);
            if (!byColumn.ContainsKey(columnName))
                byColumn[columnName] = value;
        }

        return byColumn.Values.ToArray();
    }

    private static IEntityType ResolveDiscriminatorEntityType(EntityEntry entry)
    {
        return entry.Metadata.Model.FindEntityType(entry.Entity.GetType()) ?? entry.Metadata;
    }

    private static void AddDiscriminatorProperty(IList<PropertyValue> values, IEntityType entityType, StoreObjectIdentifier storeObject)
    {
        var discriminator = entityType.FindDiscriminatorProperty();
        if (discriminator == null)
            return;

        if (discriminator.GetColumnName(storeObject) == null)
            return;

        var discriminatorValue = entityType.GetDiscriminatorValue();
        if (discriminatorValue == null)
            return;

        var discriminatorColumn = GetColumnName(discriminator, storeObject);
        for (var i = 0; i < values.Count; i++)
        {
            if (!string.Equals(GetColumnName(values[i].Property, storeObject), discriminatorColumn, StringComparison.OrdinalIgnoreCase))
                continue;

            values[i] = new PropertyValue(discriminator, discriminatorValue);
            return;
        }

        values.Add(new PropertyValue(discriminator, discriminatorValue));
    }

    private static void AddDiscriminatorProperty(IList<PropertyValue> values, IEntityType entityType)
    {
        var discriminator = entityType.FindDiscriminatorProperty();
        if (discriminator == null)
            return;

        var discriminatorValue = entityType.GetDiscriminatorValue();
        if (discriminatorValue == null)
            return;

        var discriminatorColumn = GetColumnName(discriminator);
        for (var i = 0; i < values.Count; i++)
        {
            if (!string.Equals(GetColumnName(values[i].Property), discriminatorColumn, StringComparison.OrdinalIgnoreCase))
                continue;

            values[i] = new PropertyValue(discriminator, discriminatorValue);
            return;
        }

        values.Add(new PropertyValue(discriminator, discriminatorValue));
    }

    private static bool IsOwnershipReference(ReferenceEntry reference)
    {
        return reference.Metadata is INavigation navigation && navigation.ForeignKey.IsOwnership;
    }

    private static bool IsSelectSql(string sql)
    {
        return Regex.IsMatch(sql, @"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase);
    }

    private static IReadOnlyList<string>? ExtractSelectOutputColumns(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        var text = sql.Trim();
        if (!text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return null;

        var start = 6;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        if (start + 3 <= text.Length && text.Substring(start, 3).Equals("TOP", StringComparison.OrdinalIgnoreCase))
        {
            start += 3;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;

            if (start < text.Length && text[start] == '(')
            {
                start++;
                while (start < text.Length && text[start] != ')')
                    start++;

                if (start < text.Length && text[start] == ')')
                    start++;
            }
            else
            {
                while (start < text.Length && char.IsDigit(text[start]))
                    start++;
            }

            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        var fromIndex = FindTopLevelFrom(text, start);
        if (fromIndex < 0)
            return null;

        var projection = text[start..fromIndex].Trim();
        if (string.IsNullOrWhiteSpace(projection) || projection == "*")
            return null;

        var columns = SplitTopLevel(projection, ',')
            .Select(NormalizeSelectOutput)
            .ToArray();

        return columns.Length == 0 ? null : columns;
    }

    private static int FindTopLevelFrom(string text, int start)
    {
        var inString = false;
        var depth = 0;

        for (var i = Math.Max(0, start); i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && i + 4 <= text.Length && text.Substring(i, 4).Equals("FROM", StringComparison.OrdinalIgnoreCase))
            {
                var prefixBoundary = i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] == ')';
                var suffixBoundary = i + 4 >= text.Length || char.IsWhiteSpace(text[i + 4]);
                if (prefixBoundary && suffixBoundary)
                    return i;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitTopLevel(string text, char separator)
    {
        var list = new List<string>();
        var inString = false;
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && c == separator)
            {
                list.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        if (start <= text.Length)
            list.Add(text[start..].Trim());

        return list;
    }

    private static string NormalizeSelectOutput(string column)
    {
        var trimmed = column.Trim();
        var aliasMatch = Regex.Match(trimmed, @"\s+AS\s+(?<alias>[\w\.\[\]`""']+)$", RegexOptions.IgnoreCase);
        if (aliasMatch.Success)
            return aliasMatch.Groups["alias"].Value.Trim().Trim('[', ']', '"', '`');

        return trimmed;
    }

    private static (WalhallaSqlDbConnection Connection, bool OwnsConnection) CreateExecutionContext(
        WalhallaSqlEfCoreOptions options,
        WalhallaSqlDbConnection? existingConnection)
    {
        ArgumentNullException.ThrowIfNull(options);

        var engine = options.Engine;

        if (existingConnection != null)
        {
            if (existingConnection.State != System.Data.ConnectionState.Open)
                existingConnection.Open();

            return (existingConnection, OwnsConnection: false);
        }

        var connectionString = options.ResolveConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (engine == null)
            {
                throw new InvalidOperationException(
                    "WalhallaSqlEfCoreOptions requires either WalhallaEngine or ConnectionString.");
            }

            // Use explicit engine constructor so _hasExplicitEngine = true
            // and the connection won't dispose the shared engine on close.
            var connection = new WalhallaSqlDbConnection(engine);
            connection.Open();
            return (connection, OwnsConnection: true);
        }

        var connFromCs = new WalhallaSqlDbConnection(connectionString);
        connFromCs.Open();

        return (connFromCs, OwnsConnection: true);
    }

    private static WalhallaSqlEfCoreOptions ResolveLayeredOptions(IDbContextOptions options)
    {
        var extension = options.FindExtension<WalhallaSqlDbContextOptionsExtension>();
        if (extension?.LayeredOptions != null)
            return extension.LayeredOptions;

        throw new InvalidOperationException(
            "WalhallaSql options are not configured for this DbContext. " +
            "Call UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine)) or UseWalhallaSql(new WalhallaSqlEfCoreOptions(connectionString)) on DbContextOptionsBuilder.");
    }

    private sealed record SaveChangesCommand(
        string Sql,
        bool RequiresAffectedRow,
        string? KeyDescription);

    private sealed record StoreColumnValue(string ColumnName, object? Value, IProperty? Property);

    private sealed record PropertyValue(IProperty Property, object? Value);

    private sealed record SaveChangesWorkItem(
        IUpdateEntry? RootUpdateEntry,
        EntityEntry RootEntityEntry,
        IReadOnlyList<(IUpdateEntry UpdateEntry, EntityEntry EntityEntry)> OwnedEntries,
        EntityState EffectiveState);

    private sealed class SaveChangesWorkItemBuilder
    {
        public SaveChangesWorkItemBuilder(EntityEntry rootEntry, IUpdateEntry? rootUpdateEntry)
        {
            RootUpdateEntry = rootUpdateEntry;
            RootEntityEntry = rootEntry;
        }

        public IUpdateEntry? RootUpdateEntry { get; }

        public EntityEntry RootEntityEntry { get; }

        public List<(IUpdateEntry UpdateEntry, EntityEntry EntityEntry)> Entries { get; } = new();
    }
}
