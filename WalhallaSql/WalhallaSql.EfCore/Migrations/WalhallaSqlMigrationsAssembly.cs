using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WalhallaSql.EfCore.Migrations;

public sealed class WalhallaSqlMigrationsAssembly : IMigrationsAssembly
{
    private readonly Type _contextType;

    public WalhallaSqlMigrationsAssembly(ICurrentDbContext currentDbContext)
    {
        if (currentDbContext == null)
            throw new ArgumentNullException(nameof(currentDbContext));

        _contextType = currentDbContext.Context.GetType();
        Assembly = _contextType.Assembly;
        Migrations = DiscoverMigrations(Assembly);
        ModelSnapshot = DiscoverModelSnapshot(Assembly);
    }

    public IReadOnlyDictionary<string, TypeInfo> Migrations { get; }

    public ModelSnapshot? ModelSnapshot { get; }

    public Assembly Assembly { get; }

    public string? FindMigrationId(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
            return null;

        if (Migrations.ContainsKey(nameOrId))
            return nameOrId;

        var byName = Migrations.Keys
            .FirstOrDefault(key => key.EndsWith($"_{nameOrId}", StringComparison.OrdinalIgnoreCase));

        return byName;
    }

    public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        if (migrationClass == null)
            throw new ArgumentNullException(nameof(migrationClass));

        return (Migration)Activator.CreateInstance(migrationClass.AsType())!;
    }

    private static IReadOnlyDictionary<string, TypeInfo> DiscoverMigrations(Assembly assembly)
    {
        var migrations = new Dictionary<string, TypeInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in assembly.DefinedTypes.Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type)))
        {
            var id = type.GetCustomAttribute<MigrationAttribute>()?.Id;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            migrations[id] = type;
        }

        return migrations;
    }

    private static ModelSnapshot? DiscoverModelSnapshot(Assembly assembly)
    {
        var snapshotType = assembly.DefinedTypes
            .FirstOrDefault(type => !type.IsAbstract && typeof(ModelSnapshot).IsAssignableFrom(type));

        if (snapshotType == null)
            return null;

        return (ModelSnapshot?)Activator.CreateInstance(snapshotType.AsType());
    }
}
