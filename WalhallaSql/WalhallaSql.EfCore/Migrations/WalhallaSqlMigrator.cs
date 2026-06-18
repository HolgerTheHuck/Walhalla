using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace WalhallaSql.EfCore.Migrations;

public sealed class WalhallaSqlMigrator : IMigrator
{
    private readonly ICurrentDbContext _currentDbContext;
    private readonly IMigrationsAssembly _migrationsAssembly;
    private readonly ILogger<WalhallaSqlMigrator> _logger;

    public WalhallaSqlMigrator(
        ICurrentDbContext currentDbContext,
        IMigrationsAssembly migrationsAssembly,
        ILogger<WalhallaSqlMigrator>? logger = null)
    {
        _currentDbContext = currentDbContext ?? throw new ArgumentNullException(nameof(currentDbContext));
        _migrationsAssembly = migrationsAssembly ?? throw new ArgumentNullException(nameof(migrationsAssembly));
        _logger = logger;
    }

    public void Migrate(string? targetMigration = null)
    {
        using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
        var migrations = runtime.Migrations;

        _logger?.LogInformation("Starting migration. Target: {TargetMigration}.", targetMigration ?? "latest");

        if (IsInitialDatabaseTarget(targetMigration))
        {
            _logger?.LogInformation("Downgrading to initial database (target: {TargetMigration}).", targetMigration);
            var downPlan = migrations.PlanDownMigrationToEmpty();
            if (downPlan.Operations.Count > 0)
            {
                migrations.ApplyPlan("DownToInitial", downPlan);
                _logger?.LogInformation("Downgrade applied {OperationCount} operations.", downPlan.Operations.Count);
            }
            else
            {
                _logger?.LogInformation("No tables to drop. Database is already empty.");
            }

            migrations.ClearHistory();
            _logger?.LogInformation("Migration history cleared.");
            return;
        }

        var applied = migrations.GetHistory()
            .Select(entry => entry.MigrationId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var knownMigrations = _migrationsAssembly.Migrations.Keys
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger?.LogDebug("Migration state: {AppliedCount} applied, {KnownCount} known in assembly.", applied.Count, knownMigrations.Count);

        var unknownApplied = GetUnknownAppliedMigrations(applied, knownMigrations);
        if (unknownApplied.Count > 0)
        {
            if (runtime.Options.AutoRepairHistory)
            {
                _logger?.LogWarning("Repairing migration history by removing {Count} unknown entries: {Entries}.", unknownApplied.Count, string.Join(", ", unknownApplied));
                migrations.RemoveHistoryEntries(unknownApplied);
                applied = migrations.GetHistory()
                    .Select(entry => entry.MigrationId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                var unknownText = string.Join(", ", unknownApplied);
                var dbInfo = migrations.GetDatabaseInfo();
                var locationHint = dbInfo.Location != ":memory:"
                    ? $"\nDatabase location: {dbInfo.Location} {(dbInfo.IsDirectory ? "(DIRECTORY)" : "")}"
                    : "";
                var resetHint = dbInfo.IsDirectory
                    ? $"\nTo reset database:\n  Remove-Item \"{dbInfo.Location}\" -Recurse -Force\n  dotnet ef database update"
                    : "";

                throw new InvalidOperationException(
                    "Migration history is inconsistent: applied migration(s) are missing in the current assembly set. " +
                    $"Missing: {unknownText}. " +
                    "Repair options: restore the missing migration classes, clean/repair '__ef_migrations_history', " +
                    "or run against a database initialized with the same migration assembly." +
                    locationHint + resetHint);
            }
        }

        if (knownMigrations.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(targetMigration))
            {
                _logger?.LogError("Target migration '{TargetMigration}' was specified, but no migrations are available in the assembly.", targetMigration);
                throw new InvalidOperationException(
                    $"Target migration '{targetMigration}' was specified, but no migrations are available in the assembly.");
            }

            var fallbackMigrationId = $"Auto_{DateTime.UtcNow:yyyyMMddHHmmss}";
            _logger?.LogInformation("No migrations found. Applying auto-migration '{FallbackMigrationId}'.", fallbackMigrationId);
            migrations.ApplyPlannedChanges(fallbackMigrationId);
            return;
        }

        var effectiveTarget = ResolveTargetMigrationId(targetMigration, knownMigrations);

        if (applied.Contains(effectiveTarget))
        {
            var lastApplied = knownMigrations.LastOrDefault(id => applied.Contains(id));
            if (lastApplied != null
                && string.Compare(effectiveTarget, lastApplied, StringComparison.OrdinalIgnoreCase) < 0)
            {
                _logger?.LogInformation("Rolling back from '{LastApplied}' to '{EffectiveTarget}'.", lastApplied, effectiveTarget);

                var targetModel = ExtractTargetModel(effectiveTarget);
                var downPlan = migrations.PlanDownMigration(targetModel);

                if (downPlan.Operations.Count > 0)
                {
                    migrations.ApplyPlan($"RollbackTo_{effectiveTarget}", downPlan);
                    _logger?.LogInformation("Rollback applied {OperationCount} operations.", downPlan.Operations.Count);
                }
                else
                {
                    _logger?.LogInformation("No schema changes needed for rollback.");
                }

                migrations.RemoveHistoryEntriesAfter(effectiveTarget);
                _logger?.LogInformation("Migration history trimmed to '{EffectiveTarget}'.", effectiveTarget);
                return;
            }

            _logger?.LogInformation("Database is already at migration '{EffectiveTarget}'.", effectiveTarget);
            return;
        }

        var pendingMigrations = EnumeratePendingMigrations(knownMigrations, applied, effectiveTarget).ToList();

        if (pendingMigrations.Count == 0)
        {
            _logger?.LogInformation("No pending migrations. Database is up-to-date.");
            return;
        }

        _logger?.LogInformation("Applying {PendingCount} pending migration(s) up to '{EffectiveTarget}'.", pendingMigrations.Count, effectiveTarget);

        foreach (var migrationId in pendingMigrations)
        {
            _logger?.LogInformation("Applying migration '{MigrationId}'.", migrationId);
            migrations.ApplyPlannedChanges(migrationId);
            _logger?.LogInformation("Migration '{MigrationId}' applied successfully.", migrationId);
        }

        _logger?.LogInformation("Migration complete. Applied {PendingCount} migration(s).", pendingMigrations.Count);
    }

    public Task MigrateAsync(string? targetMigration = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Migrate(targetMigration);
        return Task.CompletedTask;
    }

    public bool HasPendingModelChanges()
    {
        using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
        var applied = runtime.Migrations.GetHistory()
            .Select(entry => entry.MigrationId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var knownMigrations = _migrationsAssembly.Migrations.Keys
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return knownMigrations.Any(id => !applied.Contains(id));
    }

    public string GenerateScript(string? fromMigration = null, string? toMigration = null, MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        var isFromInitial = string.IsNullOrWhiteSpace(fromMigration) || IsInitialDatabaseTarget(fromMigration);
        var isToInitial = !string.IsNullOrWhiteSpace(toMigration) && IsInitialDatabaseTarget(toMigration);
        var isToLatest = string.IsNullOrWhiteSpace(toMigration) || IsLatestMigrationTarget(toMigration);

        if (isFromInitial && isToLatest)
        {
            _logger?.LogInformation("Generating full create script for current model.");
            using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
            return runtime.Migrations.GenerateCreateScript();
        }

        var knownMigrations = _migrationsAssembly.Migrations.Keys
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? resolvedFrom = null;
        string? resolvedTo = null;

        if (!isFromInitial)
            resolvedFrom = ResolveTargetMigrationId(fromMigration!, knownMigrations);

        if (!isToLatest && !isToInitial)
            resolvedTo = ResolveTargetMigrationId(toMigration!, knownMigrations);

        IModel fromModel;
        if (resolvedFrom == null)
        {
            var emptyBuilder = new ModelBuilder(new Microsoft.EntityFrameworkCore.Metadata.Conventions.ConventionSet());
            fromModel = emptyBuilder.FinalizeModel();
        }
        else
        {
            fromModel = ExtractTargetModel(resolvedFrom);
        }

        IModel toModel;
        if (isToInitial)
        {
            var emptyBuilder = new ModelBuilder(new Microsoft.EntityFrameworkCore.Metadata.Conventions.ConventionSet());
            toModel = emptyBuilder.FinalizeModel();
        }
        else if (resolvedTo != null)
        {
            toModel = ExtractTargetModel(resolvedTo);
        }
        else
        {
            toModel = _currentDbContext.Context.Model;
        }

        _logger?.LogInformation(
            "Generating incremental script from '{FromMigration}' to '{ToMigration}'.",
            fromMigration ?? "0",
            toMigration ?? "latest");

        using var scriptRuntime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
        var plan = scriptRuntime.Migrations.PlanModelChanges(fromModel, toModel);

        if (plan.Operations.Count == 0)
            return "-- No changes.";

        var lines = plan.Operations
            .Select(op => WalhallaSqlMigrationScriptBuilder.BuildMigrationSql(op))
            .ToList();

        return string.Join(";\n", lines) + ";";
    }

    private bool IsLatestMigrationTarget(string? targetMigration)
    {
        if (string.IsNullOrWhiteSpace(targetMigration))
            return true;

        if (IsInitialDatabaseTarget(targetMigration))
            return false;

        var resolved = _migrationsAssembly.FindMigrationId(targetMigration);
        if (string.IsNullOrWhiteSpace(resolved))
            return false;

        var knownMigrations = _migrationsAssembly.Migrations.Keys
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (knownMigrations.Count == 0)
            return true;

        return string.Equals(resolved, knownMigrations[^1], StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveTargetMigrationId(string? targetMigration, IReadOnlyList<string> orderedMigrations)
    {
        if (orderedMigrations.Count == 0)
            throw new InvalidOperationException("No migrations available.");

        if (string.IsNullOrWhiteSpace(targetMigration))
            return orderedMigrations[^1];

        var resolved = _migrationsAssembly.FindMigrationId(targetMigration);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                $"Target migration '{targetMigration}' could not be resolved. " +
                "Use an existing migration name or id.");
        }

        if (!orderedMigrations.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Resolved target migration '{resolved}' does not exist in the ordered migration set.");
        }

        return resolved;
    }

    private static bool IsInitialDatabaseTarget(string? targetMigration)
    {
        return string.Equals(targetMigration, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetMigration, Migration.InitialDatabase, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumeratePendingMigrations(
        IReadOnlyList<string> orderedMigrations,
        HashSet<string> applied,
        string? targetMigration)
    {
        foreach (var migrationId in orderedMigrations)
        {
            if (applied.Contains(migrationId))
            {
                if (string.Equals(migrationId, targetMigration, StringComparison.OrdinalIgnoreCase))
                    yield break;

                continue;
            }

            yield return migrationId;

            if (string.Equals(migrationId, targetMigration, StringComparison.OrdinalIgnoreCase))
                yield break;
        }
    }

    private static IReadOnlyList<string> GetUnknownAppliedMigrations(
        HashSet<string> appliedMigrations,
        IReadOnlyList<string> knownMigrations)
    {
        if (appliedMigrations.Count == 0)
            return Array.Empty<string>();

        var knownSet = knownMigrations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownApplied = appliedMigrations
            .Where(applied => !knownSet.Contains(applied))
            .Where(applied => !IsLegacyAutoMigrationId(applied))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return unknownApplied;
    }

    private static bool IsLegacyAutoMigrationId(string migrationId)
    {
        return migrationId.StartsWith("Auto_", StringComparison.OrdinalIgnoreCase);
    }

    private IModel ExtractTargetModel(string migrationId)
    {
        var migrationType = _migrationsAssembly.Migrations[migrationId];
        var migration = (Migration)Activator.CreateInstance(migrationType)!;

        // Prefer extracting the model by instantiating the target DbContext,
        // since that gives us a fully-conventioned model identical to what
        // the migration's BuildTargetModel would produce.
        var dbContextAttr = migrationType.GetCustomAttribute<DbContextAttribute>();
        if (dbContextAttr != null)
        {
            var dbContextType = dbContextAttr.ContextType;
            var connectionString = _currentDbContext.Context.Database.GetDbConnection().ConnectionString;

            try
            {
                var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(dbContextType);
                var builder = Activator.CreateInstance(builderType)!;
                var optionsBuilder = (DbContextOptionsBuilder)builder;
                optionsBuilder.UseWalhallaSql(connectionString);
                var optionsProperty = builderType.GetProperty(
                    "Options",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
                var options = optionsProperty.GetValue(builder)!;
                var context = (DbContext)Activator.CreateInstance(dbContextType, options)!;
                return context.Model;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Could not instantiate target DbContext '{DbContextType}' for migration '{MigrationId}'. Falling back to BuildTargetModel.",
                    dbContextType.Name,
                    migrationId);
            }
        }

        var modelBuilder = new ModelBuilder(new Microsoft.EntityFrameworkCore.Metadata.Conventions.ConventionSet());
        var method = migration.GetType().GetMethod(
            "BuildTargetModel",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (method == null)
        {
            throw new InvalidOperationException(
                $"Migration '{migrationId}' does not have a BuildTargetModel method. " +
                "Ensure the migration was generated by EF Core tools.");
        }

        method.Invoke(migration, new object[] { modelBuilder });
        var model = modelBuilder.FinalizeModel();

        var initializer = _currentDbContext.Context.GetService<IModelRuntimeInitializer>();
        return initializer.Initialize(model);
    }
}
