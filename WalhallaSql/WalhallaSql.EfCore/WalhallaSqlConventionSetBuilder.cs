using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlConventionSetBuilder : RelationalConventionSetBuilder
{
    public WalhallaSqlConventionSetBuilder(
        ProviderConventionSetBuilderDependencies dependencies,
        RelationalConventionSetBuilderDependencies relationalDependencies)
        : base(dependencies, relationalDependencies)
    {
    }

    public override ConventionSet CreateConventionSet()
    {
        var conventionSet = base.CreateConventionSet();
        conventionSet.ModelFinalizingConventions.Add(new JsonContainerColumnTypeConvention());
        conventionSet.ModelFinalizingConventions.Add(new ValidTableNameConvention());
        conventionSet.ModelFinalizingConventions.Add(new SharedTableAutoIncludeConvention());
        return conventionSet;
    }

    /// <summary>
    /// Stellt sicher, dass JSON-gemappte Typen (Owned Entities / Complex Types) einen
    /// provider-spezifischen Container-Spaltentyp erhalten. EF Core 10 verlangt diesen
    /// für <see cref="RelationalModel.CreateContainerColumn"/>; wir verwenden "JSON".
    /// </summary>
    private sealed class JsonContainerColumnTypeConvention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(
            IConventionModelBuilder modelBuilder,
            IConventionContext<IConventionModelBuilder> context)
        {
            foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
            {
                if (entityType.IsMappedToJson()
                    && string.IsNullOrEmpty(entityType.GetContainerColumnType()))
                {
                    entityType.SetContainerColumnType("JSON");
                }
            }
        }
    }

    private sealed class ValidTableNameConvention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(
            IConventionModelBuilder modelBuilder,
            IConventionContext<IConventionModelBuilder> context)
        {
            var rewrittenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entityType in modelBuilder.Metadata.GetEntityTypes().OrderBy(static entity => entity.Name, StringComparer.Ordinal))
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrWhiteSpace(tableName))
                    continue;

                if (rewrittenNames.TryGetValue(tableName, out var rewrittenName))
                {
                    entityType.Builder.ToTable(rewrittenName, entityType.GetSchema());
                    continue;
                }

                var sanitizedName = WalhallaSqlStoreObjectNameSanitizer.Sanitize(tableName);
                rewrittenName = WalhallaSqlStoreObjectNameSanitizer.MakeUnique(sanitizedName, usedNames);
                rewrittenNames[tableName] = rewrittenName;

                if (!string.Equals(tableName, rewrittenName, StringComparison.Ordinal))
                    entityType.Builder.ToTable(rewrittenName, entityType.GetSchema());
            }
        }
    }

    private sealed class SharedTableAutoIncludeConvention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(
            IConventionModelBuilder modelBuilder,
            IConventionContext<IConventionModelBuilder> context)
        {
            foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
            {
                foreach (var navigation in entityType.GetNavigations())
                {
                    if (navigation.IsCollection)
                        continue;

                    var foreignKey = navigation.ForeignKey;
                    if (foreignKey.IsOwnership || !foreignKey.IsUnique)
                        continue;

                    if (!ReferenceEquals(foreignKey.PrincipalEntityType, entityType))
                        continue;

                    if (ReferenceEquals(foreignKey.DeclaringEntityType, entityType))
                        continue;

                    if (!IsSharedTableReference(entityType, foreignKey.DeclaringEntityType))
                        continue;

                    navigation.Builder.AutoInclude(true);
                }
            }
        }

        private static bool IsSharedTableReference(IConventionEntityType principalType, IConventionEntityType dependentType)
        {
            var principalTable = principalType.GetTableName();
            var dependentTable = dependentType.GetTableName();

            if (string.IsNullOrWhiteSpace(principalTable)
                || string.IsNullOrWhiteSpace(dependentTable)
                || !string.Equals(principalTable, dependentTable, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(principalType.GetSchema(), dependentType.GetSchema(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
