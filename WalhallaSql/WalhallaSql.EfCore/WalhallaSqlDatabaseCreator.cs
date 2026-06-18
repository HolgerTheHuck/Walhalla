using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlDatabaseCreator : IRelationalDatabaseCreator
{
    private readonly ICurrentDbContext _currentDbContext;

    public WalhallaSqlDatabaseCreator(ICurrentDbContext currentDbContext)
    {
        _currentDbContext = currentDbContext ?? throw new ArgumentNullException(nameof(currentDbContext));
    }

    public bool EnsureDeleted()
    {
        Delete();
        return true;
    }

    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delete();
        return Task.FromResult(true);
    }

    public bool EnsureCreated()
    {
        var context = _currentDbContext.Context;
        using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);

        if (runtime.Migrations.GetHistory().Count > 0)
            return false;

        var migrationId = $"EnsureCreated_{DateTime.UtcNow:yyyyMMddHHmmss}";
        runtime.Migrations.ApplyPlannedChanges(migrationId);
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;
        ApplyModelSeedData(runtime, designTimeModel);
        return true;
    }

    private static void ApplyModelSeedData(WalhallaSqlDbContextRuntime runtime, IModel model)
    {
        foreach (var entityType in GetSeedEntityTypesInInsertOrder(model, runtime.ResolveCollectionName))
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrWhiteSpace(tableName))
                continue;

            var collectionName = runtime.ResolveCollectionName(entityType);

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            var ownership = entityType.FindOwnership();
            var sharesTableWithPrincipal = ownership != null
                && SharesTableWithPrincipal(entityType, ownership, runtime.ResolveCollectionName);

            foreach (var seedRow in entityType.GetSeedData())
            {
                var properties = entityType.GetProperties()
                    .Where(property => property.GetColumnName(storeObject) != null && seedRow.ContainsKey(property.Name))
                    .Select(property => (Property: property, Value: seedRow[property.Name]))
                    .ToList();

                AddDiscriminatorProperty(entityType, storeObject, properties);

                if (properties.Count == 0)
                    continue;

                if (sharesTableWithPrincipal)
                {
                    ApplySharedTableOwnedSeedData(runtime, collectionName, storeObject, ownership!, properties, seedRow);
                    continue;
                }

                var columns = string.Join(", ", properties.Select(property => property.Property.GetColumnName(storeObject)));
                var values = string.Join(", ", properties.Select(property => WalhallaSqlEfCoreSqlRenderer.FormatSqlLiteral(property.Value, property.Property)));
                runtime.ExecuteSql($"INSERT INTO {collectionName} ({columns}) VALUES ({values})");
            }
        }
    }

    private static void ApplySharedTableOwnedSeedData(
        WalhallaSqlDbContextRuntime runtime,
        string collectionName,
        StoreObjectIdentifier storeObject,
        IForeignKey ownership,
        IList<(IProperty Property, object? Value)> properties,
        IDictionary<string, object?> seedRow)
    {
        var keyProperties = ownership.Properties
            .Where(property => property.GetColumnName(storeObject) != null && seedRow.ContainsKey(property.Name))
            .Select(property => (Property: property, Value: seedRow[property.Name]))
            .ToArray();

        var assignments = properties
            .Where(property => !keyProperties.Any(key => string.Equals(
                key.Property.GetColumnName(storeObject),
                property.Property.GetColumnName(storeObject),
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (assignments.Length == 0 || keyProperties.Length == 0)
            return;

        var setSql = string.Join(", ", assignments.Select(property =>
            $"{property.Property.GetColumnName(storeObject)} = {WalhallaSqlEfCoreSqlRenderer.FormatSqlLiteral(property.Value, property.Property)}"));

        var whereSql = string.Join(" AND ", keyProperties.Select(property =>
            $"{property.Property.GetColumnName(storeObject)} = {WalhallaSqlEfCoreSqlRenderer.FormatSqlLiteral(property.Value, property.Property)}"));

        runtime.ExecuteSql($"UPDATE {collectionName} SET {setSql} WHERE {whereSql}");
    }

    private static void AddDiscriminatorProperty(
        IEntityType entityType,
        StoreObjectIdentifier storeObject,
        IList<(IProperty Property, object? Value)> properties)
    {
        var discriminator = entityType.FindDiscriminatorProperty();
        if (discriminator == null)
            return;

        var discriminatorValue = entityType.GetDiscriminatorValue();
        if (discriminatorValue == null)
            return;

        var columnName = discriminator.GetColumnName(storeObject);
        if (string.IsNullOrWhiteSpace(columnName))
            return;

        for (var i = 0; i < properties.Count; i++)
        {
            if (!string.Equals(properties[i].Property.GetColumnName(storeObject), columnName, StringComparison.OrdinalIgnoreCase))
                continue;

            properties[i] = (discriminator, discriminatorValue);
            return;
        }

        properties.Add((discriminator, discriminatorValue));
    }

    private static bool SharesTableWithPrincipal(
        IEntityType entityType,
        IForeignKey ownership,
        Func<IEntityType, string> collectionNameResolver)
    {
        var entityCollection = collectionNameResolver(entityType);
        var principalCollection = collectionNameResolver(ownership.PrincipalEntityType);
        return string.Equals(entityCollection, principalCollection, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IEntityType> GetSeedEntityTypesInInsertOrder(IModel model, Func<IEntityType, string> collectionNameResolver)
    {
        var seededTypes = model.GetEntityTypes()
            .Where(entityType => !string.IsNullOrWhiteSpace(entityType.GetTableName()) && entityType.GetSeedData().Any())
            .OrderBy(entityType => entityType.Name, StringComparer.Ordinal)
            .ToArray();

        var seededTypeSet = seededTypes.ToHashSet();
        var dependencies = seededTypes.ToDictionary(
            entityType => entityType,
            entityType => entityType.GetForeignKeys()
                .SelectMany(foreignKey => seededTypes.Where(candidate =>
                    candidate != entityType
                    && IsSeedPrincipalDependencyCandidate(candidate, foreignKey.PrincipalEntityType, collectionNameResolver)))
                .Where(seededTypeSet.Contains)
                .ToHashSet());

        var ready = new Queue<IEntityType>(dependencies
            .Where(pair => pair.Value.Count == 0)
            .Select(pair => pair.Key)
            .OrderBy(entityType => entityType.Name, StringComparer.Ordinal));

        var ordered = new List<IEntityType>(seededTypes.Length);
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            ordered.Add(current);

            foreach (var dependent in seededTypes.Where(candidate => dependencies[candidate].Contains(current)))
            {
                dependencies[dependent].Remove(current);
                if (dependencies[dependent].Count == 0)
                    ready.Enqueue(dependent);
            }
        }

        if (ordered.Count == seededTypes.Length)
            return ordered;

        foreach (var remaining in seededTypes.Where(entityType => !ordered.Contains(entityType)).OrderBy(entityType => entityType.Name, StringComparer.Ordinal))
            ordered.Add(remaining);

        return ordered;
    }

    private static bool IsSeedPrincipalDependencyCandidate(
        IEntityType candidate,
        IEntityType principalEntityType,
        Func<IEntityType, string> collectionNameResolver)
    {
        if (candidate == principalEntityType)
            return true;

        if (candidate.GetRootType() == principalEntityType.GetRootType())
            return true;

        return string.Equals(
            collectionNameResolver(candidate),
            collectionNameResolver(principalEntityType),
            StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EnsureCreated());
    }

    public bool CanConnect()
    {
        try
        {
            using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CanConnect());
    }

    public bool Exists()
    {
        return CanConnect();
    }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Exists());
    }

    public bool HasTables()
    {
        using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
        return runtime.Migrations.GetHistory().Count > 0;
    }

    public Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(HasTables());
    }

    public void Create()
    {
    }

    public Task CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Create();
        return Task.CompletedTask;
    }

    public void Delete()
    {
        var context = _currentDbContext.Context;
        string? location = null;
        bool isDirectory = false;

        try
        {
            using var runtime = WalhallaSqlDbContextRuntime.Create(context);
            var dbInfo = runtime.Migrations.GetDatabaseInfo();
            location = dbInfo.Location;
            isDirectory = dbInfo.IsDirectory;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(location) || location == ":memory:")
            return;

        try
        {
            if (isDirectory)
            {
                if (System.IO.Directory.Exists(location))
                    System.IO.Directory.Delete(location, recursive: true);
            }
            else
            {
                if (System.IO.File.Exists(location))
                    System.IO.File.Delete(location);
            }
        }
        catch
        {
            // Best-effort deletion.
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delete();
        return Task.CompletedTask;
    }

    public void CreateTables()
    {
        if (!HasTables())
            EnsureCreated();
    }

    public Task CreateTablesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateTables();
        return Task.CompletedTask;
    }

    public string GenerateCreateScript()
    {
        using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
        return runtime.Migrations.GenerateCreateScript();
    }
}
