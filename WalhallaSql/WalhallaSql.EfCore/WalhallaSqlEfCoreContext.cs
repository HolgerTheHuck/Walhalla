using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql.AdoNet;
using WalhallaSql.AdoNet.SqlClient;
using WalhallaSql.EfCore.Linq;
using WalhallaSql.EfCore.Migrations;
using WalhallaSql.Sql;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace WalhallaSql.EfCore;

/// <summary>
/// Base <see cref="DbContext"> for applications using WalhallaSql as the database provider.
/// Provides direct access to the underlying engine and migration services.
/// </summary>
public abstract class WalhallaSqlEfCoreContext : DbContext
{
    private readonly WalhallaSqlEfCoreOptions _layeredOptions;
    private readonly WalhallaSqlDbConnection _sqlConnection;
    private WalhallaSqlMigrationService? _migrationService;

    protected WalhallaSqlEfCoreContext(DbContextOptions options)
        : base(options)
    {
        _layeredOptions = ResolveLayeredOptions(options);
        _sqlConnection = CreateExecutionContext(_layeredOptions);
    }

    protected WalhallaSqlEfCoreContext(DbContextOptions options, WalhallaSqlEfCoreOptions layeredOptions)
        : base(options)
    {
        _layeredOptions = layeredOptions ?? throw new ArgumentNullException(nameof(layeredOptions));
        _sqlConnection = CreateExecutionContext(_layeredOptions);
    }

    public WalhallaSqlDatabaseInfo GetDatabaseInfo()
    {
        return _sqlConnection.GetStorageInfo();
    }

    public WalhallaSqlMigrationService Migrations =>
        _migrationService ??= new WalhallaSqlMigrationService(
            this,
            _sqlConnection,
            _layeredOptions,
            logger: ResolveLogger(this));

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
    public WalhallaSqlLinqQuery<TEntity> Query<TEntity>(string collectionName)
        where TEntity : class
    {
        return new WalhallaSqlLinqQuery<TEntity>(this, collectionName);
    }

    public SqlExecutionResult ExecuteSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL must not be empty.", nameof(sql));

        return ExecuteSqlInternal(sql, transaction: null);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        return ExecuteSaveChangesCore(
            CancellationToken.None,
            () => base.SaveChanges(acceptAllChangesOnSuccess));
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteSaveChangesCoreAsync(
            cancellationToken,
            () => base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken));
    }

    private int ExecuteSaveChangesCore(CancellationToken cancellationToken, Func<int> saveChanges)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Database.CurrentTransaction != null)
        {
            throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.ExternalEfTransaction,
                $"SaveChanges for '{GetType().Name}' does not support an ambient EF transaction on the WalhallaSqlEfCoreContext path.",
                "Use a plain DbContext configured with UseWalhallaSql(...) and enlist explicitly via DatabaseFacade.UseTransaction(...) when this transaction shape is required.");
        }

        // Disable AutoDetectChanges for the entire save sequence so we can control
        // exactly when DetectChanges runs.  With AutoDetectChanges enabled, any call
        // to ChangeTracker.Entries() would trigger value generators (from the
        // in-memory provider) which would assign a generated key value and clear
        // IsTemporary before our guardrail can see it.
        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;
        ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            // Step 1: Materialize generated single-column keys before SQL is built.
            var addedEntries = ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToArray();
            var generatedKeyChanges = MaterializeGeneratedKeys(addedEntries);
            PropagateGeneratedForeignKeys(ChangeTracker.Entries().ToArray(), generatedKeyChanges);

            // Step 2: Capture currently-Modified entries before DetectChanges for
            // the no-op update guard (same semantics as before).
            var originallyModifiedEntries = ChangeTracker
                .Entries()
                .Where(entry => entry.State == EntityState.Modified)
                .ToArray();

            // Step 3: Run DetectChanges explicitly once.
            ChangeTracker.DetectChanges();

            ValidateNoOpModifiedEntries(originallyModifiedEntries);

            return saveChanges();
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private async Task<int> ExecuteSaveChangesCoreAsync(CancellationToken cancellationToken, Func<Task<int>> saveChangesAsync)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Database.CurrentTransaction != null)
        {
            throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.ExternalEfTransaction,
                $"SaveChanges for '{GetType().Name}' does not support an ambient EF transaction on the WalhallaSqlEfCoreContext path.",
                "Use a plain DbContext configured with UseWalhallaSql(...) and enlist explicitly via DatabaseFacade.UseTransaction(...) when this transaction shape is required.");
        }

        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;
        ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var addedEntries = ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToArray();
            var generatedKeyChanges = MaterializeGeneratedKeys(addedEntries);
            PropagateGeneratedForeignKeys(ChangeTracker.Entries().ToArray(), generatedKeyChanges);

            var originallyModifiedEntries = ChangeTracker
                .Entries()
                .Where(entry => entry.State == EntityState.Modified)
                .ToArray();

            ChangeTracker.DetectChanges();

            ValidateNoOpModifiedEntries(originallyModifiedEntries);

            return await saveChangesAsync().ConfigureAwait(false);
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private SqlExecutionResult ExecuteSqlInternal(string sql, DbTransaction? transaction)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL must not be empty.", nameof(sql));

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

            return new SqlExecutionResult(rows.Count, rows);
        }

        var affected = command.ExecuteNonQuery();
        return new SqlExecutionResult(affected);
    }

    private SaveChangesCommand? BuildCommandForWorkItem(SaveChangesWorkItem workItem)
    {
        return workItem.EffectiveState switch
        {
            EntityState.Added => BuildInsertCommand(workItem),
            EntityState.Modified => BuildUpdateSql(workItem),
            EntityState.Deleted => BuildDeleteSql(workItem),
            _ => null
        };
    }

    private SaveChangesCommand BuildInsertCommand(SaveChangesWorkItem workItem)
    {
        var entry = workItem.RootEntry;
        var collectionName = ResolveCollectionName(entry.Metadata);

        var properties = GetInsertProperties(workItem);

        if (properties.Length == 0)
            throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.NoMappedScalarProperties,
                $"SaveChanges INSERT for '{entry.Metadata.Name}' requires at least one mapped scalar property.",
                "Map at least one scalar property or avoid persisting this entity via SaveChanges MVP path.");

        var columns = string.Join(", ", properties.Select(property => GetColumnName(property.Property)));
        var values = string.Join(", ", properties.Select(property => WalhallaSqlEfCoreSqlRenderer.FormatSqlLiteral(property.Value, property.Property)));

        return new SaveChangesCommand(
            $"INSERT INTO {collectionName} ({columns}) VALUES ({values})",
            RequiresAffectedRow: false,
            KeyDescription: null);
    }

    private SaveChangesCommand? BuildUpdateSql(SaveChangesWorkItem workItem)
    {
        var entry = workItem.RootEntry;
        var collectionName = ResolveCollectionName(entry.Metadata);
        var keyProperties = GetPrimaryKeyProperties(entry);
        var concurrencyProperties = WalhallaSqlSaveChangesSupport.GetConcurrencyProperties(entry);

        var modifiedProperties = GetUpdateProperties(workItem);

        if (modifiedProperties.Length == 0)
            throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.NoOpModifiedEntry,
                $"SaveChanges UPDATE for '{entry.Metadata.Name}' has no modified scalar properties.",
                "Mark at least one scalar property as modified or avoid calling SaveChanges for no-op updates.");

        var setSql = string.Join(", ", modifiedProperties.Select(property => $"{GetColumnName(property.Property)} = {WalhallaSqlEfCoreSqlRenderer.FormatSqlLiteral(property.Value, property.Property)}"));
        var whereSql = WalhallaSqlSaveChangesSupport.BuildKeyAndConcurrencyPredicate(
            entry,
            collectionName,
            keyProperties,
            concurrencyProperties,
            property => entry.Property(property.Name).CurrentValue,
            property =>
            {
                var propertyEntry = entry.Property(property.Name);
                return propertyEntry.OriginalValue ?? propertyEntry.CurrentValue;
            },
            "UPDATE");
        return new SaveChangesCommand(
            $"UPDATE {collectionName} SET {setSql} WHERE {whereSql}",
            RequiresAffectedRow: true,
            KeyDescription: BuildPrimaryKeyDescription(
                entry,
                keyProperties,
                property => entry.Property(property.Name).CurrentValue));
    }

    private SaveChangesCommand BuildDeleteSql(SaveChangesWorkItem workItem)
    {
        var entry = workItem.RootEntry;
        var collectionName = ResolveCollectionName(entry.Metadata);
        var keyProperties = GetPrimaryKeyProperties(entry);
        var concurrencyProperties = WalhallaSqlSaveChangesSupport.GetConcurrencyProperties(entry);
        var whereSql = WalhallaSqlSaveChangesSupport.BuildKeyAndConcurrencyPredicate(
            entry,
            collectionName,
            keyProperties,
            concurrencyProperties,
            property =>
            {
                var keyEntry = entry.Property(property.Name);
                return keyEntry.OriginalValue ?? keyEntry.CurrentValue;
            },
            property =>
            {
                var propertyEntry = entry.Property(property.Name);
                return propertyEntry.OriginalValue ?? propertyEntry.CurrentValue;
            },
            "DELETE");

        return new SaveChangesCommand(
            $"DELETE FROM {collectionName} WHERE {whereSql}",
            RequiresAffectedRow: true,
            KeyDescription: BuildPrimaryKeyDescription(
                entry,
                keyProperties,
                property =>
                {
                    var keyEntry = entry.Property(property.Name);
                    return keyEntry.OriginalValue ?? keyEntry.CurrentValue;
                }));
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

    private IReadOnlyList<GeneratedKeyChange> MaterializeGeneratedKeys(IEnumerable<EntityEntry> addedEntries)
    {
        var addedEntryArray = addedEntries.ToArray();
        var nextGeneratedNumericKeys = new Dictionary<GeneratedKeySequenceKey, object?>();
        var generatedKeyChanges = new List<GeneratedKeyChange>();

        foreach (var entry in addedEntryArray)
        {
            var primaryKey = entry.Metadata.FindPrimaryKey();
            if (primaryKey == null || primaryKey.Properties.Count != 1)
                continue;

            var keyProperty = primaryKey.Properties[0];
            if (keyProperty.ValueGenerated != ValueGenerated.OnAdd)
                continue;

            // Read the actual CLR property value from the C# object, not from EF Core's
            // tracking store.  EF Core's change tracker may have already generated
            // a value for the property (e.g. via the in-memory value generator during Add()),
            // making IsTemporary unreliable.  The C# object still holds the original value
            // (= 0 for int, null for ref types) when the user did not set it explicitly.
            var clrValue = keyProperty.GetGetter().GetClrValue(entry.Entity);
            if (!keyEntry_IsTemporaryOrDefault(entry, keyProperty, clrValue))
                continue;

            var keyEntry = entry.Property(keyProperty.Name);
            var previousValue = keyEntry.CurrentValue;
            keyEntry.CurrentValue = GenerateKeyValue(entry, keyProperty, addedEntryArray, nextGeneratedNumericKeys);
            keyEntry.IsTemporary = false;

            if (!Equals(previousValue, keyEntry.CurrentValue))
            {
                generatedKeyChanges.Add(new GeneratedKeyChange(
                    entry,
                    keyProperty,
                    previousValue,
                    keyEntry.CurrentValue));
            }
        }

        return generatedKeyChanges;

        static bool keyEntry_IsTemporaryOrDefault(EntityEntry entry, IProperty keyProperty, object? clrValue)
        {
            // IsTemporary: EF Core set a placeholder value (not yet replaced by a real generated key)
            var keyEntry = entry.Property(keyProperty.Name);
            if (keyEntry.IsTemporary)
                return true;

            // CLR value is the type default (user did not set it → relying on DB/provider generation)
            return IsNullOrDefault(clrValue, keyProperty.ClrType);
        }
    }

    private object GenerateKeyValue(
        EntityEntry entry,
        IProperty keyProperty,
        IReadOnlyList<EntityEntry> addedEntries,
        IDictionary<GeneratedKeySequenceKey, object?> nextGeneratedNumericKeys)
    {
        var clrType = Nullable.GetUnderlyingType(keyProperty.ClrType) ?? keyProperty.ClrType;
        if (clrType == typeof(Guid))
            return Guid.NewGuid();

        var collectionName = ResolveCollectionName(entry.Metadata);
        var columnName = GetColumnName(keyProperty);
        var sequenceKey = new GeneratedKeySequenceKey(collectionName, columnName, clrType);

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

        var nextValue = GenerateNextNumericKey(currentMax, clrType, entry.Metadata.Name);
        nextGeneratedNumericKeys[sequenceKey] = nextValue;
        return nextValue;
    }

    private IEnumerable<object> EnumerateExplicitBatchKeyValues(
        IReadOnlyList<EntityEntry> addedEntries,
        GeneratedKeySequenceKey sequenceKey)
    {
        foreach (var entry in addedEntries)
        {
            var primaryKey = entry.Metadata.FindPrimaryKey();
            if (primaryKey == null || primaryKey.Properties.Count != 1)
                continue;

            var keyProperty = primaryKey.Properties[0];
            if (keyProperty.ValueGenerated != ValueGenerated.OnAdd)
                continue;

            if (!string.Equals(ResolveCollectionName(entry.Metadata), sequenceKey.CollectionName, StringComparison.Ordinal)
                || !string.Equals(GetColumnName(keyProperty), sequenceKey.ColumnName, StringComparison.Ordinal))
            {
                continue;
            }

            var clrValue = keyProperty.GetGetter().GetClrValue(entry.Entity);
            var keyEntry = entry.Property(keyProperty.Name);
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

    private readonly record struct GeneratedKeySequenceKey(string CollectionName, string ColumnName, Type ClrType);

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

    private static void ValidateSaveChangesEntries(EntityEntry[] entries)
    {
        var changedEntries = entries
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        foreach (var entry in changedEntries)
        {
            GetPrimaryKeyProperties(entry);
            WalhallaSqlSaveChangesSupport.ValidatePropertyConstraints(entry);

            var hasChangedReferenceGraph = entry.References.Any(reference =>
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

    private static void ValidateNoOpModifiedEntries(EntityEntry[] originallyModifiedEntries)
    {
        foreach (var entry in originallyModifiedEntries)
        {
            if (entry.State != EntityState.Unchanged)
                continue;

            var hasModifiedScalarProperties = entry.Properties
                .Where(property => !property.Metadata.IsPrimaryKey())
                .Any(property => property.IsModified);

            if (hasModifiedScalarProperties)
                continue;

            throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.NoOpModifiedEntry,
                $"SaveChanges UPDATE for '{entry.Metadata.Name}' has no modified scalar properties.",
                "Mark at least one scalar property as modified or avoid calling SaveChanges for no-op updates.");
        }
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

    private static object? ToProviderValue(object? clrValue, IProperty property)
    {
        var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;
        if (converter != null)
            return converter.ConvertToProvider(clrValue);

        // No explicit converter present (e.g. UseInMemoryDatabase does not set one).
        // Apply common CLR-to-SQL-literal conversions so the engine receives a SQL-safe value.
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

    private IReadOnlyList<SaveChangesWorkItem> BuildSaveChangesWorkItems(EntityEntry[] entries)
    {
        var trackedEntries = ChangeTracker.Entries().ToArray();
        var changedEntries = entries
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        var byRoot = new Dictionary<object, SaveChangesWorkItemBuilder>();
        foreach (var entry in changedEntries)
        {
            var rootEntry = GetAggregateRootEntry(entry, trackedEntries);
            if (!byRoot.TryGetValue(rootEntry.Entity, out var builder))
            {
                builder = new SaveChangesWorkItemBuilder(rootEntry);
                byRoot[rootEntry.Entity] = builder;
            }

            builder.Entries.Add(entry);
        }

        var workItems = byRoot.Values
            .Select(builder => CreateWorkItem(builder))
            .Where(item => item.EffectiveState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        ValidateOwnedTypeMappings(workItems);
        return OrderWorkItems(workItems);
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

    private SaveChangesWorkItem CreateWorkItem(SaveChangesWorkItemBuilder builder)
    {
        var ownedEntries = builder.Entries
            .Where(entry => !ReferenceEquals(entry, builder.RootEntry))
            .ToArray();

        var effectiveState = builder.RootEntry.State switch
        {
            EntityState.Added => EntityState.Added,
            EntityState.Deleted => EntityState.Deleted,
            EntityState.Modified => EntityState.Modified,
            _ when ownedEntries.Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) => EntityState.Modified,
            _ => EntityState.Unchanged
        };

        return new SaveChangesWorkItem(builder.RootEntry, ownedEntries, effectiveState);
    }

    private void ValidateOwnedTypeMappings(IReadOnlyList<SaveChangesWorkItem> workItems)
    {
        foreach (var workItem in workItems)
        {
            var rootCollection = ResolveCollectionName(workItem.RootEntry.Metadata);
            foreach (var ownedEntry in workItem.OwnedEntries)
            {
                var mappedTable = ownedEntry.Metadata.GetTableName();
                if (!string.IsNullOrEmpty(mappedTable)
                    && !string.Equals(rootCollection, mappedTable, StringComparison.OrdinalIgnoreCase))
                {
                    throw EfSaveChangesGuardrail.NotSupportedWithHint(
                        EfSaveChangesGuardrail.Codes.OwnedTypes,
                        $"Owned entity type '{ownedEntry.Metadata.Name}' mapped to a different table is not supported in SaveChanges MVP.",
                        "Keep owned types table-split with their owner or persist them outside the current SaveChanges path.");
                }
            }
        }
    }

    private IReadOnlyList<SaveChangesWorkItem> OrderWorkItems(IReadOnlyList<SaveChangesWorkItem> workItems)
    {
        var dependencies = workItems.ToDictionary(item => item, _ => new HashSet<SaveChangesWorkItem>());
        var dependents = workItems.ToDictionary(item => item, _ => new HashSet<SaveChangesWorkItem>());

        foreach (var item in workItems)
        {
            foreach (var foreignKey in item.RootEntry.Metadata.GetForeignKeys())
            {
                if (foreignKey.IsOwnership)
                    continue;

                var targetItem = workItems.FirstOrDefault(candidate =>
                    MatchesEntityTypeOrDerived(candidate.RootEntry.Metadata, foreignKey.PrincipalEntityType) &&
                    ForeignKeyMatches(item.RootEntry, candidate.RootEntry, foreignKey, useOriginalValues: false));
                if (targetItem == null)
                    continue;

                if (ReferenceEquals(item, targetItem))
                    continue;

                TryAddRelationshipDependency(item, targetItem, foreignKey, dependencies, dependents);
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
        SaveChangesWorkItem principalItem,
        IForeignKey foreignKey,
        IReadOnlyDictionary<SaveChangesWorkItem, HashSet<SaveChangesWorkItem>> dependencies,
        IReadOnlyDictionary<SaveChangesWorkItem, HashSet<SaveChangesWorkItem>> dependents)
    {
        var currentMatch = ForeignKeyMatches(dependentItem.RootEntry, principalItem.RootEntry, foreignKey, useOriginalValues: false);
        var originalMatch = ForeignKeyMatches(dependentItem.RootEntry, principalItem.RootEntry, foreignKey, useOriginalValues: true);

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

            foreach (var foreignKey in item.RootEntry.Metadata.GetForeignKeys())
            {
                if (foreignKey.IsOwnership)
                    continue;

                if (workItems.Any(candidate =>
                    !ReferenceEquals(candidate, item)
                    && candidate.EffectiveState == EntityState.Added
                    && MatchesEntityTypeOrDerived(candidate.RootEntry.Metadata, foreignKey.PrincipalEntityType)
                    && ForeignKeyMatches(item.RootEntry, candidate.RootEntry, foreignKey, useOriginalValues: false)))
                {
                    return true;
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

            foreach (var foreignKey in item.RootEntry.Metadata.GetForeignKeys())
            {
                if (foreignKey.IsOwnership)
                    continue;

                if (workItems.Any(candidate =>
                    !ReferenceEquals(candidate, item)
                    && candidate.EffectiveState == EntityState.Deleted
                    && MatchesEntityTypeOrDerived(candidate.RootEntry.Metadata, foreignKey.PrincipalEntityType)
                    && ForeignKeyMatches(item.RootEntry, candidate.RootEntry, foreignKey, useOriginalValues: true)))
                {
                    return true;
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

    private static bool MatchesEntityTypeOrDerived(IEntityType candidate, IEntityType target)
    {
        for (var current = candidate; current != null; current = current.BaseType)
        {
            if (ReferenceEquals(current, target))
                return true;
        }

        return false;
    }

    private PropertyValue[] GetInsertProperties(SaveChangesWorkItem workItem)
    {
        var values = new List<PropertyValue>();

        values.AddRange(workItem.RootEntry.Properties
            .Where(property => property.Metadata.ValueGenerated != ValueGenerated.OnAdd || property.CurrentValue != null)
            .Select(property => new PropertyValue(property.Metadata, property.CurrentValue)));

        AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(workItem.RootEntry));

        foreach (var ownedEntry in workItem.OwnedEntries)
        {
            values.AddRange(GetOwnedInsertProperties(ownedEntry));
        }

        return DistinctPropertiesByColumn(values);
    }

    private PropertyValue[] GetUpdateProperties(SaveChangesWorkItem workItem)
    {
        var values = new List<PropertyValue>();

        values.AddRange(workItem.RootEntry.Properties
            .Where(property => property.IsModified)
            .Where(property => !property.Metadata.IsPrimaryKey())
            .Select(property => new PropertyValue(property.Metadata, property.CurrentValue)));

        foreach (var ownedEntry in workItem.OwnedEntries)
            values.AddRange(GetOwnedUpdateProperties(ownedEntry));

        return DistinctPropertiesByColumn(values);
    }

    private static IEnumerable<PropertyValue> GetOwnedInsertProperties(EntityEntry ownedEntry)
    {
        var values = new List<PropertyValue>();

        foreach (var property in ownedEntry.Properties)
        {
            if (property.Metadata.IsPrimaryKey())
                continue;

            values.Add(new PropertyValue(property.Metadata, property.CurrentValue));
        }

        AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(ownedEntry));

        foreach (var value in values)
            yield return value;
    }

    private static IEnumerable<PropertyValue> GetOwnedUpdateProperties(EntityEntry ownedEntry)
    {
        var values = new List<PropertyValue>();

        foreach (var property in ownedEntry.Properties)
        {
            if (property.Metadata.IsPrimaryKey())
                continue;

            if (ownedEntry.State == EntityState.Deleted)
            {
                values.Add(new PropertyValue(property.Metadata, null));
                continue;
            }

            if (ownedEntry.State == EntityState.Added || property.IsModified)
                values.Add(new PropertyValue(property.Metadata, property.CurrentValue));
        }

        AddDiscriminatorProperty(values, ResolveDiscriminatorEntityType(ownedEntry));

        foreach (var value in values)
            yield return value;
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

    private sealed record SaveChangesCommand(
        string Sql,
        bool RequiresAffectedRow,
        string? KeyDescription);

    private sealed record PropertyValue(IProperty Property, object? Value);

    private sealed record SaveChangesWorkItem(EntityEntry RootEntry, IReadOnlyList<EntityEntry> OwnedEntries, EntityState EffectiveState);

    private sealed class SaveChangesWorkItemBuilder
    {
        public SaveChangesWorkItemBuilder(EntityEntry rootEntry)
        {
            RootEntry = rootEntry;
        }

        public EntityEntry RootEntry { get; }

        public List<EntityEntry> Entries { get; } = new();
    }

    protected virtual void OnSaveChangesCommandExecuted(EntityEntry entry, SqlExecutionResult result)
    {
    }

    internal void NotifySaveChangesCommandExecuted(EntityEntry entry, SqlExecutionResult result)
        => OnSaveChangesCommandExecuted(entry, result);

    internal string ResolveCollectionName(IEntityType entityType)
    {
        if (entityType == null)
            throw new ArgumentNullException(nameof(entityType));

        if (_layeredOptions.CollectionNameResolver != null)
            return _layeredOptions.CollectionNameResolver(entityType);

        return WalhallaSqlStoreObjectNameSanitizer.ResolveDefaultCollectionName(entityType);
    }

    private static string? TryExtractPrimaryCollectionName(string sql)
    {
        var normalized = sql.Trim();

        var fromMatch = Regex.Match(normalized, @"\bFROM\s+([\w\.]+)", RegexOptions.IgnoreCase);
        if (fromMatch.Success)
            return fromMatch.Groups[1].Value;

        var updateMatch = Regex.Match(normalized, @"^UPDATE\s+([\w\.]+)", RegexOptions.IgnoreCase);
        if (updateMatch.Success)
            return updateMatch.Groups[1].Value;

        var insertMatch = Regex.Match(normalized, @"^INSERT\s+INTO\s+([\w\.]+)", RegexOptions.IgnoreCase);
        if (insertMatch.Success)
            return insertMatch.Groups[1].Value;

        var dropTableMatch = Regex.Match(normalized, @"^DROP\s+TABLE\s+([\w\.]+)", RegexOptions.IgnoreCase);
        if (dropTableMatch.Success)
            return dropTableMatch.Groups[1].Value;

        var dropIndexMatch = Regex.Match(normalized, @"^DROP\s+INDEX\s+\w+\s+ON\s+([\w\.]+)", RegexOptions.IgnoreCase);
        if (dropIndexMatch.Success)
            return dropIndexMatch.Groups[1].Value;

        return null;
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

    private static WalhallaSqlDbConnection CreateExecutionContext(WalhallaSqlEfCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var engine = options.Engine;
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
            return connection;
        }

        var connFromCs = new WalhallaSqlDbConnection(connectionString);
        connFromCs.Open();

        return connFromCs;
    }

    private static WalhallaSqlEfCoreOptions ResolveLayeredOptions(DbContextOptions options)
    {
        var extension = options.FindExtension<WalhallaSqlDbContextOptionsExtension>();
        if (extension?.LayeredOptions != null)
            return extension.LayeredOptions;

        throw new InvalidOperationException(
            "WalhallaSql options are not configured for this DbContext. " +
            "Call UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine)) or UseWalhallaSql(new WalhallaSqlEfCoreOptions(connectionString)) on DbContextOptionsBuilder " +
            "or use the constructor overload that accepts WalhallaSqlEfCoreOptions.");
    }

    public override void Dispose()
    {
        _sqlConnection.Dispose();
        base.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        _sqlConnection.Dispose();
        await base.DisposeAsync();
    }
}
