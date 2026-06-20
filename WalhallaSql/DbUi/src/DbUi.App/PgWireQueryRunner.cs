using System.Data.Common;
using System.Diagnostics;
using DbUi.Core.Providers;
using DbUi.Core.Queries;

namespace DbUi.App;

public sealed class PgWireQueryRunner : IQueryRunner
{
    private const int MaxRows = 10_000;
    private readonly Func<DbConnection> _connectionProvider;

    public PgWireQueryRunner(Func<DbConnection> connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task<QueryResult> ExecuteAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                stopwatch.Stop();
                return new QueryResult
                {
                    ErrorMessage = "Query text is empty.",
                    Elapsed = stopwatch.Elapsed,
                };
            }

            var connection = _connectionProvider();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = request.Text;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var columns = new List<QueryColumn>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(new QueryColumn(reader.GetName(i), reader.GetFieldType(i)));

            var rows = new List<object?[]>();
            var rowCount = 0;
            while (rowCount < MaxRows && await reader.ReadAsync(cancellationToken))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
                rowCount++;
            }

            var affectedRows = reader.RecordsAffected >= 0 ? reader.RecordsAffected : rows.Count;

            stopwatch.Stop();

            return new QueryResult
            {
                Columns = columns,
                Rows = rows,
                AffectedRows = affectedRows,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new QueryResult
            {
                ErrorMessage = ex.Message,
                Elapsed = stopwatch.Elapsed,
            };
        }
    }
}
