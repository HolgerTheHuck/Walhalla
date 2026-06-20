using DbUi.Core.Catalog;
using DbUi.Core.Diagnostics;
using DbUi.Core.Queries;

namespace DbUi.Core.Workspace;

public interface IWorkspaceSession : IAsyncDisposable
{
    string SessionId { get; }

    string DisplayName { get; }

    string? CurrentDatabase { get; }

    ICatalogBrowser Catalog { get; }

    IQueryRunner Queries { get; }

    IExplainProvider? Explain { get; }

    IDiagnosticsProvider? Diagnostics { get; }

    IMaintenanceProvider? Maintenance { get; }

    Task<IReadOnlyList<string>> GetAvailableDatabasesAsync(
        CancellationToken cancellationToken = default);

    Task ChangeDatabaseAsync(
        string databaseName,
        CancellationToken cancellationToken = default);
}
