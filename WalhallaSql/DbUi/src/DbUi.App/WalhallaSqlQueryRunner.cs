using System.Data;
using System.Data.Common;
using System.Diagnostics;
using DbUi.Core.Providers;
using DbUi.Core.Queries;
using WalhallaSql.AdoNet;
using WalhallaSql.Sql;

namespace DbUi.App;

public sealed class WalhallaSqlQueryRunner : IQueryRunner
{
    private const int MaxRows = 10_000;
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

            // Letztes Statement mit Reader ausfuehren, damit Ergebnisdaten angezeigt werden.
            var lastSql = statements[^1];
            using var lastCmd = connection.CreateCommand();
            lastCmd.CommandText = lastSql;

            await using var reader = await lastCmd.ExecuteReaderAsync(cancellationToken);

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

            var affectedRows = reader.RecordsAffected >= 0
                ? totalAffectedRows + reader.RecordsAffected
                : totalAffectedRows + rows.Count;

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
