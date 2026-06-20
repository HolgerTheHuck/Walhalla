using System.Data.Common;
using DbUi.Core.Catalog;
using DbUi.Core.Diagnostics;
using DbUi.Core.Queries;
using DbUi.Core.Workspace;
using Npgsql;

namespace DbUi.App;

public sealed class PgWireWorkspaceSession : IWorkspaceSession
{
    private readonly NpgsqlConnection _connection;
    private readonly string _databaseName;

    public PgWireWorkspaceSession(NpgsqlConnection connection, string displayName)
    {
        _connection = connection;
        _databaseName = connection.Database;
        SessionId = Guid.NewGuid().ToString("N");
        DisplayName = displayName;

        Catalog = new PgWireCatalogBrowser(() => _connection, displayName);
        Queries = new PgWireQueryRunner(() => _connection);
        Maintenance = new PgWireMaintenanceProvider(() => _connection);
    }

    public string SessionId { get; }

    public string DisplayName { get; }

    public string? CurrentDatabase => _databaseName;

    public ICatalogBrowser Catalog { get; }

    public IQueryRunner Queries { get; }

    public IExplainProvider? Explain => null;

    public IDiagnosticsProvider? Diagnostics => null;

    public IMaintenanceProvider? Maintenance { get; }

    public async Task<IReadOnlyList<string>> GetAvailableDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var databases = new List<string>();
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            databases.Add(reader.GetString(0));

        return databases;
    }

    public async Task ChangeDatabaseAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        // Npgsql unterstuetzt ChangeDatabase nicht direkt; Verbindung muss neu aufgebaut werden.
        throw new NotSupportedException("Datenbankwechsel wird fuer PgWire-Verbindungen nicht unterstuetzt.");
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
