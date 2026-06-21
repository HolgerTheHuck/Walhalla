using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DbUi.Core.Collections;
using DbUi.Core.Providers;
using DbUi.Core.Queries;
using WalhallaSql.AdoNet;
using WalhallaSql.Sql;

namespace DbUi.App;

public sealed class WalhallaSqlQueryRunner : IQueryRunner
{
    private const int BatchSize = 1000;
    private const long DefaultMaxRows = 1_000_000;
    private readonly Func<WalhallaSqlDbConnection> _connectionProvider;

    public WalhallaSqlQueryRunner(Func<WalhallaSqlDbConnection> connectionProvider)
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

            // Alle Statements bis auf das letzte als NonQuery ausfuehren,
            // damit Skripte wie "CREATE TABLE ...; INSERT ...; SELECT ..." funktionieren.
            for (int i = 0; i < statements.Count - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = statements[i];
                var affected = cmd.ExecuteNonQuery();
                if (affected >= 0)
                    totalAffectedRows += affected;
            }

            // Letztes Statement: SELECT-artig mit Reader streamen, sonst NonQuery.
            var lastSql = statements[^1];
            using var lastCmd = connection.CreateCommand();
            lastCmd.CommandText = lastSql;

            if (!IsSelectLikeStatement(lastSql))
            {
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

            var reader = await lastCmd.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);

            var columns = ReadColumns(reader);
            var stream = StreamRowsAsync(reader, columns, cancellationToken);
            var streamingCollection = new StreamingRowCollection(stream, DefaultMaxRows);

            // RecordsAffected ist erst nach vollstaendigem Lesen des Readers verlaesslich;
            // wir melden daher 0 fuer SELECT-Streams.
            var affectedRows = totalAffectedRows;

            stopwatch.Stop();

            return new QueryResult
            {
                Columns = columns,
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

    private static IReadOnlyList<QueryColumn> ReadColumns(DbDataReader reader)
    {
        var columns = new List<QueryColumn>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
            columns.Add(new QueryColumn(reader.GetName(i), reader.GetFieldType(i)));
        return columns;
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?[]>> StreamRowsAsync(
        DbDataReader reader,
        IReadOnlyList<QueryColumn> columns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new List<object?[]>(BatchSize);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                batch.Add(values);

                if (batch.Count >= BatchSize)
                {
                    yield return batch;
                    batch = new List<object?[]>(BatchSize);
                }
            }

            if (batch.Count > 0)
                yield return batch;
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Grobe Heuristik, ob ein Statement ein Resultset zurueckgibt. Einfache Kommentare
    /// werden uebersprungen, damit Statements wie "/* comment */\nSELECT ..." erkannt
    /// werden.
    /// </summary>
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

            if (span.StartsWith("--") || span.StartsWith("//"))
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
