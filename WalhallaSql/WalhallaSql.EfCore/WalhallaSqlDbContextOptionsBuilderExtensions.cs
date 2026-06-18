using System;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace WalhallaSql.EfCore;

/// <summary>
/// Extension methods for configuring a <see cref="DbContext"> to use WalhallaSql as the underlying provider.
/// </summary>
public static class WalhallaSqlDbContextOptionsBuilderExtensions
{
    internal static DbContextOptionsBuilder UseWalhallaSql(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<WalhallaSqlDbContextOptionsBuilder>? WalhallaSqlOptionsAction = null)
    {
        if (optionsBuilder == null)
            throw new ArgumentNullException(nameof(optionsBuilder));

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));

        ConfigureWalhallaSql(optionsBuilder, new WalhallaSqlEfCoreOptions(connectionString));
        WalhallaSqlOptionsAction?.Invoke(new WalhallaSqlDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }

    public static DbContextOptionsBuilder UseWalhallaSql(
        this DbContextOptionsBuilder optionsBuilder,
        WalhallaSqlEfCoreOptions layeredOptions)
    {
        if (optionsBuilder == null)
            throw new ArgumentNullException(nameof(optionsBuilder));

        if (layeredOptions == null)
            throw new ArgumentNullException(nameof(layeredOptions));

        ConfigureWalhallaSql(optionsBuilder, layeredOptions);

        return optionsBuilder;
    }

    public static DbContextOptionsBuilder UseWalhallaSql(
        this DbContextOptionsBuilder optionsBuilder,
        DbConnection connection,
        Action<WalhallaSqlDbContextOptionsBuilder>? WalhallaSqlOptionsAction = null)
    {
        if (optionsBuilder == null)
            throw new ArgumentNullException(nameof(optionsBuilder));

        if (connection == null)
            throw new ArgumentNullException(nameof(connection));

        var layeredOptions = new WalhallaSqlEfCoreOptions(connection.ConnectionString ?? string.Empty);
        ConfigureWalhallaSql(optionsBuilder, layeredOptions, connection);
        WalhallaSqlOptionsAction?.Invoke(new WalhallaSqlDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }

    internal static DbContextOptionsBuilder<TContext> UseWalhallaSql<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        Action<WalhallaSqlDbContextOptionsBuilder>? WalhallaSqlOptionsAction = null)
        where TContext : DbContext
    {
        UseWalhallaSql((DbContextOptionsBuilder)optionsBuilder, connectionString, WalhallaSqlOptionsAction);
        return optionsBuilder;
    }

    public static DbContextOptionsBuilder<TContext> UseWalhallaSql<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        WalhallaSqlEfCoreOptions layeredOptions)
        where TContext : DbContext
    {
        UseWalhallaSql((DbContextOptionsBuilder)optionsBuilder, layeredOptions);
        return optionsBuilder;
    }

    public static DbContextOptionsBuilder<TContext> UseWalhallaSql<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        DbConnection connection,
        Action<WalhallaSqlDbContextOptionsBuilder>? WalhallaSqlOptionsAction = null)
        where TContext : DbContext
    {
        UseWalhallaSql((DbContextOptionsBuilder)optionsBuilder, connection, WalhallaSqlOptionsAction);
        return optionsBuilder;
    }

    private static void ConfigureWalhallaSql(
        DbContextOptionsBuilder optionsBuilder,
        WalhallaSqlEfCoreOptions layeredOptions,
        DbConnection? connection = null)
    {
        var extension = optionsBuilder.Options.FindExtension<WalhallaSqlDbContextOptionsExtension>()
            ?? new WalhallaSqlDbContextOptionsExtension();

        var configuredExtension = extension.WithLayeredOptions(layeredOptions);
        if (connection != null)
            configuredExtension = configuredExtension.WithExistingConnection(connection);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(configuredExtension);

        ConfigureWarnings(optionsBuilder);
    }

    private static void ConfigureWarnings(DbContextOptionsBuilder optionsBuilder)
    {
        var coreOptionsExtension = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()
            ?? new CoreOptionsExtension();

        coreOptionsExtension = RelationalOptionsExtension.WithDefaultWarningConfiguration(coreOptionsExtension);
        coreOptionsExtension = coreOptionsExtension.WithWarningsConfiguration(
            coreOptionsExtension.WarningsConfiguration.WithExplicit(
                [RelationalEventId.MultipleCollectionIncludeWarning],
                WarningBehavior.Log));

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(coreOptionsExtension);
    }
}
