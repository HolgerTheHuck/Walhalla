using WalhallaSql.EfCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WalhallaSql.EfCore;

public static class WalhallaSqlServiceCollectionExtensions
{
    public static IServiceCollection AddEntityFrameworkWalhallaSql(this IServiceCollection services)
    {
        var builder = new EntityFrameworkRelationalServicesBuilder(services);

        builder.TryAdd<LoggingDefinitions, WalhallaSqlLoggingDefinitions>();
        builder.TryAdd<IDatabaseProvider, DatabaseProvider<WalhallaSqlDbContextOptionsExtension>>();
        builder.TryAdd<IDatabase, WalhallaSqlDatabase>();
        builder.TryAdd<IRelationalConnection, WalhallaSqlRelationalConnection>();
        builder.TryAdd<IRelationalTransactionFactory, WalhallaSqlRelationalTransactionFactory>();
        builder.TryAdd<IExecutionStrategyFactory, WalhallaSqlExecutionStrategyFactory>();
        builder.TryAdd<ISqlGenerationHelper, WalhallaSqlSqlGenerationHelper>();
        builder.TryAdd<IRelationalTypeMappingSource, WalhallaSqlTypeMappingSource>();
        builder.TryAdd<IQuerySqlGeneratorFactory, WalhallaSqlQuerySqlGeneratorFactory>();
        builder.TryAdd<IQueryableMethodTranslatingExpressionVisitorFactory, WalhallaSqlQueryableMethodTranslatingExpressionVisitorFactory>();
        builder.TryAdd<IRelationalSqlTranslatingExpressionVisitorFactory, WalhallaSqlSqlTranslatingExpressionVisitorFactory>();
        builder.TryAdd<IProviderConventionSetBuilder, WalhallaSqlConventionSetBuilder>();
        builder.TryAdd<IUpdateSqlGenerator, WalhallaSqlUpdateSqlGenerator>();
        builder.TryAdd<IModificationCommandBatchFactory, WalhallaSqlModificationCommandBatchFactory>();
        builder.TryAdd<IRelationalDatabaseCreator, WalhallaSqlDatabaseCreator>();
        builder.TryAdd<IMigrationsAssembly, WalhallaSqlMigrationsAssembly>();
        builder.TryAdd<IMigrator, WalhallaSqlMigrator>();
        builder.TryAdd<IHistoryRepository, WalhallaSqlHistoryRepository>();
        builder.TryAddCoreServices();

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IInterceptor, WalhallaSqlFilteredIncludeQueryExpressionInterceptor>());
        services.TryAddSingleton<WalhallaSqlLoggingDefinitions>();
        services.TryAddSingleton<RelationalLoggingDefinitions>(provider => provider.GetRequiredService<WalhallaSqlLoggingDefinitions>());

        return services;
    }
}
