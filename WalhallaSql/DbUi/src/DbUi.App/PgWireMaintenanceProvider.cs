using DbUi.Core.Diagnostics;

namespace DbUi.App;

public sealed class PgWireMaintenanceProvider : IMaintenanceProvider
{
    private readonly Func<System.Data.Common.DbConnection> _connectionProvider;

    public PgWireMaintenanceProvider(Func<System.Data.Common.DbConnection> connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        // PostgreSQL unterstuetzt CHECKPOINT nur mit Superuser-Rechten;
        // gegen WalhallaSql.PgWire ist dies aktuell nicht implementiert.
        throw new NotSupportedException("CHECKPOINT wird fuer PgWire-Verbindungen nicht unterstuetzt.");
    }

    public async Task<int> VacuumAsync(string? tableName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sql = tableName is null
            ? "VACUUM"
            : $"VACUUM {EscapeName(tableName)}";

        return await ExecuteNonQueryAsync(sql, cancellationToken);
    }

    public async Task AnalyzeAsync(string? tableName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sql = tableName is null
            ? "ANALYZE"
            : $"ANALYZE {EscapeName(tableName)}";

        await ExecuteNonQueryAsync(sql, cancellationToken);
    }

    public Task BackupAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("BACKUP wird fuer PgWire-Verbindungen nicht unterstuetzt.");
    }

    private async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = _connectionProvider();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EscapeName(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
