using DbUi.Core.Diagnostics;
using WalhallaSql;

namespace DbUi.App;

public sealed class WalhallaSqlMaintenanceProvider : IMaintenanceProvider
{
    private readonly WalhallaEngine _engine;

    public WalhallaSqlMaintenanceProvider(WalhallaEngine engine)
    {
        _engine = engine;
    }

    public Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _engine.Checkpoint();
        return Task.CompletedTask;
    }

    public Task<int> VacuumAsync(string? tableName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _engine.Vacuum(tableName);
        return Task.FromResult(result);
    }

    public Task AnalyzeAsync(string? tableName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sql = tableName is null
            ? "ANALYZE"
            : $"ANALYZE {EscapeName(tableName)}";

        _engine.Execute(sql);
        return Task.CompletedTask;
    }

    public Task BackupAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        _engine.Backup(targetPath);
        return Task.CompletedTask;
    }

    private static string EscapeName(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}
