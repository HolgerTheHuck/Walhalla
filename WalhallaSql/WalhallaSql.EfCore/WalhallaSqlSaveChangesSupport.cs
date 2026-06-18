using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace WalhallaSql.EfCore;

internal static class WalhallaSqlSaveChangesSupport
{
    private const string UpdateStoreErrorMessage = "An error occurred while saving the entity changes. See the inner exception for details.";

    public static void ValidatePropertyConstraints(EntityEntry entry)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified))
            return;

        foreach (var propertyEntry in entry.Properties)
        {
            if (entry.State == EntityState.Modified && !propertyEntry.IsModified)
                continue;

            var property = propertyEntry.Metadata;
            if (property.IsPrimaryKey())
                continue;

            var currentValue = propertyEntry.CurrentValue;
            if (currentValue == null
                && property.ValueGenerated is ValueGenerated.OnAdd or ValueGenerated.OnAddOrUpdate)
            {
                continue;
            }

            if (!IsColumnNullable(property) && currentValue == null)
            {
                throw CreateDbUpdateException(
                    $"Property '{property.Name}' on '{entry.Metadata.Name}' is required but its current value is null.");
            }

            var maxLength = property.GetMaxLength();
            if (!maxLength.HasValue)
                continue;

            if (currentValue is string text && text.Length > maxLength.Value)
            {
                throw CreateDbUpdateException(
                    $"Property '{property.Name}' on '{entry.Metadata.Name}' exceeds configured max length {maxLength.Value}. Actual length: {text.Length}.");
            }

            if (currentValue is byte[] bytes && bytes.Length > maxLength.Value)
            {
                throw CreateDbUpdateException(
                    $"Property '{property.Name}' on '{entry.Metadata.Name}' exceeds configured max length {maxLength.Value}. Actual length: {bytes.Length}.");
            }
        }
    }

    public static IReadOnlyList<IProperty> GetConcurrencyProperties(EntityEntry entry)
    {
        return entry.Metadata.GetFlattenedProperties()
            .Where(property => property.IsConcurrencyToken)
            .Where(property => !property.IsPrimaryKey())
            .ToArray();
    }

    public static string BuildKeyAndConcurrencyPredicate(
        EntityEntry entry,
        string collectionName,
        IReadOnlyList<IProperty> keyProperties,
        IReadOnlyList<IProperty> concurrencyProperties,
        Func<IProperty, object?> keyValueAccessor,
        Func<IProperty, object?> concurrencyValueAccessor,
        string operation)
    {
        var filters = new List<(string ColumnName, object? Value, IProperty? Property)>(keyProperties.Count + concurrencyProperties.Count);

        foreach (var keyProperty in keyProperties)
        {
            var keyValue = keyValueAccessor(keyProperty);
            if (keyValue == null)
            {
                throw EfSaveChangesGuardrail.NotSupportedWithHint(
                    EfSaveChangesGuardrail.Codes.NonNullPrimaryKey,
                    $"SaveChanges {operation} for '{entry.Metadata.Name}' requires non-null primary key values.",
                    "Ensure every primary key property is set before calling SaveChanges.");
            }

            filters.Add((GetColumnName(keyProperty), keyValue, keyProperty));
        }

        foreach (var concurrencyProperty in concurrencyProperties)
        {
            filters.Add((
                GetColumnName(concurrencyProperty),
                concurrencyValueAccessor(concurrencyProperty),
                concurrencyProperty));
        }

        return WalhallaSqlEfCoreSqlRenderer.RenderEqualityWhereClause(collectionName, filters)
            ?? throw EfSaveChangesGuardrail.NotSupportedWithHint(
                EfSaveChangesGuardrail.Codes.SingleColumnPrimaryKey,
                $"SaveChanges {operation} for '{entry.Metadata.Name}' could not translate the key/concurrency predicate.",
                "Use key and concurrency token types that can be converted into WalhallaSql scalar values.");
    }

    private static bool IsColumnNullable(IProperty property)
    {
        var storeObject = TryResolveStoreObject(property);
        if (storeObject.HasValue)
        {
            if (property.GetColumnName(storeObject.Value) != null)
                return property.IsColumnNullable(storeObject.Value);
        }

        return property.IsNullable;
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

    private static DbUpdateException CreateDbUpdateException(string details)
        => new(UpdateStoreErrorMessage, new InvalidOperationException(details));
}
