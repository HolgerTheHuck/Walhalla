using DbUi.Core.Catalog;
using DbUi.Core.Diagnostics;
using DbUi.Core.Queries;
using DbUi.Core.Workspace;
using WalhallaSql;
using WalhallaSql.AdoNet;

namespace DbUi.App;

public sealed class WalhallaSqlWorkspaceSession : IWorkspaceSession
{
    private readonly WalhallaEngine _engine;
    private readonly WalhallaSqlDbConnection _connection;
    private string _databaseName;

    public WalhallaSqlWorkspaceSession(
        WalhallaEngine engine,
        string storagePath,
        string databaseName,
        string displayName)
    {
        _engine = engine;
        _databaseName = databaseName;
        StoragePath = storagePath;
        SessionId = Guid.NewGuid().ToString("N");
        DisplayName = displayName;

        _connection = new WalhallaSqlDbConnection(engine);
        _connection.Open();

        Catalog = new WalhallaSqlCatalogBrowser(
            () => _engine,
            () => _databaseName,
            () => StoragePath);
        Queries = new WalhallaSqlQueryRunner(() => _connection);
    }

    public string SessionId { get; }

    public string DisplayName { get; }

    public string StoragePath { get; }

    public string? CurrentDatabase => _databaseName;

    public ICatalogBrowser Catalog { get; }

    public IQueryRunner Queries { get; }

    public IExplainProvider? Explain => null;

    public IDiagnosticsProvider? Diagnostics => null;

    public IMaintenanceProvider? Maintenance => new WalhallaSqlMaintenanceProvider(_engine);

    public Task<IReadOnlyList<string>> GetAvailableDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>([_databaseName]);
    }

    public Task ChangeDatabaseAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        var normalizedName = databaseName.Trim();
        if (string.Equals(_databaseName, normalizedName, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        _connection.ChangeDatabase(normalizedName);
        _databaseName = normalizedName;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _engine.Dispose();
    }
}
