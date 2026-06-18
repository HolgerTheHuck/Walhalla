using DbUi.Core.Providers;

namespace DbUi.Core.Queries;

public interface IQueryRunner
{
    Task<QueryResult> ExecuteAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default);
}
