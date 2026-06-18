using DbUi.Core.Queries;

namespace DbUi.Core.Diagnostics;

public interface IExplainProvider
{
    Task<string> ExplainAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default);
}
