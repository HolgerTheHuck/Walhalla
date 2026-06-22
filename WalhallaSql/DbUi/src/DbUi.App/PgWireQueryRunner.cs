using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DbUi.Core.Collections;
using DbUi.Core.Providers;
using DbUi.Core.Queries;
using WalhallaSql.Sql;

namespace DbUi.App;

public sealed class PgWireQueryRunner : IQueryRunner
{
    private const int BatchSize = 1000;
    private const long DefaultMaxRows = 1_000_000;
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

            var statements = SqlScriptSplitter.Split(request.Text);
            if (statements.Count == 0)
            {
                stopwatch.Stop();
                return new QueryResult
                {
                    ErrorMessage = "Query text is empty.",
                    Elapsed = stopwatch.Elapsed,
                };
            }

            var connection = _connectionProvider();
            var totalAffectedRows = 0;

            for (int i = 0; i < statements.Count - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = statements[i];
                var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (affected >= 0)
                    totalAffectedRows += affected;
            }

            var lastSql = statements[^1];

            if (!IsSelectLikeStatement(lastSql))
            {
                await using var lastCmd = connection.CreateCommand();
                lastCmd.CommandText = lastSql;
                var affected = await lastCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (affected >= 0)
                    totalAffectedRows += affected;

                stopwatch.Stop();
                return new QueryResult
                {
                    Columns = [],
                    Rows = [],
                    AffectedRows = totalAffectedRows,
                    Elapsed = stopwatch.Elapsed,
                };
            }

            // Konsistent zum WalhallaSql-Runner: Reader erst innerhalb des Streams
            // oeffnen, damit Spalten und Zeilen auf demselben Hintergrund-Thread
            // konsumiert werden. Der Command lebt solange wie der Stream.
            var streamingCmd = connection.CreateCommand();
            streamingCmd.CommandText = lastSql;
            var stream = StreamRowsAsync(streamingCmd, cancellationToken);
            var streamingCollection = new StreamingRowCollection(stream, DefaultMaxRows);

            // Kurzer synchroner Preload, damit das Grid nie an eine spaltenlose
            // Collection gebunden wird.
            var preloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preloadCts.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await streamingCollection.WaitForRowsAsync(1, preloadCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timeout ist okay.
            }
            finally
            {
                preloadCts.Dispose();
            }

            var affectedRows = totalAffectedRows;

            stopwatch.Stop();

            return new QueryResult
            {
                Columns = streamingCollection.Columns ?? [],
                Rows = streamingCollection,
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

    private static async IAsyncEnumerable<(IReadOnlyList<QueryColumn>? Columns, IReadOnlyList<object?[]> Rows)> StreamRowsAsync(
        DbCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess, cancellationToken);

            var columns = ReadColumns(reader);
            yield return (columns, []);

            var batch = new List<object?[]>(BatchSize);
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                batch.Add(values);

                if (batch.Count >= BatchSize)
                {
                    yield return (null, batch);
                    batch = new List<object?[]>(BatchSize);
                }
            }

            if (batch.Count > 0)
                yield return (null, batch);
        }
        finally
        {
            await command.DisposeAsync();
        }
    }

    private static IReadOnlyList<QueryColumn> ReadColumns(DbDataReader reader)
    {
        var columns = new List<QueryColumn>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
            columns.Add(new QueryColumn(reader.GetName(i), reader.GetFieldType(i)));
        return columns;
    }

    private static bool IsSelectLikeStatement(string sql)
    {
        var span = sql.AsSpan().Trim();
        while (span.Length > 0)
        {
            if (span.StartsWith("/*"))
            {
                var end = span.IndexOf("*/");
                if (end < 0) break;
                span = span.Slice(end + 2).Trim();
                continue;
            }

            if (span.StartsWith("--"))
            {
                var end = span.IndexOf('\n');
                if (end < 0) break;
                span = span.Slice(end + 1).Trim();
                continue;
            }

            break;
        }

        return span.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }
}
