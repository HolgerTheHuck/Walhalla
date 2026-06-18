namespace DbUi.Core.Diagnostics;

public interface IDiagnosticsProvider
{
    Task<IReadOnlyList<string>> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default);
}
