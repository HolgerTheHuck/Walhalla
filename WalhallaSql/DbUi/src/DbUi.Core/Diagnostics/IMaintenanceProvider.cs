namespace DbUi.Core.Diagnostics;

public interface IMaintenanceProvider
{
    Task CheckpointAsync(CancellationToken cancellationToken = default);

    Task<int> VacuumAsync(string? tableName = null, CancellationToken cancellationToken = default);

    Task AnalyzeAsync(string? tableName = null, CancellationToken cancellationToken = default);

    Task BackupAsync(string targetPath, CancellationToken cancellationToken = default);
}
