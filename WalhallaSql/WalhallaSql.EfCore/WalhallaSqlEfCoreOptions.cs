using System;
using Microsoft.EntityFrameworkCore.Metadata;

namespace WalhallaSql.EfCore;

public sealed class WalhallaSqlEfCoreOptions
{
    public WalhallaSqlEfCoreOptions(WalhallaEngine engine, string? connectionString = null)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
    }

    public WalhallaSqlEfCoreOptions(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));

        ConnectionString = connectionString;
    }

    public WalhallaEngine? Engine { get; }

    public string? ConnectionString { get; }

    public string? DataPath { get; init; }

    public Func<IEntityType, string>? CollectionNameResolver { get; init; }

    public TimeSpan MigrationLockWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MigrationLockStaleThreshold { get; init; } = TimeSpan.FromMinutes(10);

    public bool AutoRepairHistory { get; init; }

    public bool StrictMigrationGuardrails { get; init; } = true;

    public static WalhallaSqlEfCoreOptions ForEmbeddedPath(string dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
            throw new ArgumentException("Data path must not be empty.", nameof(dataPath));

        return new WalhallaSqlEfCoreOptions($"EmbeddedPath={dataPath}") { DataPath = dataPath };
    }

    public string ResolveConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
            return ConnectionString!;

        if (!string.IsNullOrWhiteSpace(DataPath))
            return $"EmbeddedPath={DataPath}";

        return string.Empty;
    }

    public string? ResolveDataPath()
    {
        if (!string.IsNullOrWhiteSpace(DataPath))
            return DataPath;

        if (string.IsNullOrWhiteSpace(ConnectionString))
            return null;

        var embeddedMatch = System.Text.RegularExpressions.Regex.Match(
            ConnectionString,
            @"(?:^|;)\s*(EmbeddedPath|File)\s*=\s*(?<value>[^;]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (embeddedMatch.Success)
            return embeddedMatch.Groups["value"].Value.Trim();

        return null;
    }
}
