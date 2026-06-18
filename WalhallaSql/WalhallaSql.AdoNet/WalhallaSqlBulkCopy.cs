using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using WalhallaSql.AdoNet.SqlClient;

namespace WalhallaSql.AdoNet;

/// <summary>
/// Provides high-throughput bulk INSERT for <see cref="WalhallaSqlDbConnection"/>.
/// All rows are written inside a single engine transaction so that the operation
/// is atomic and significantly faster than individual auto-commit INSERTs.
/// </summary>
public sealed class WalhallaSqlBulkCopy : IDisposable
{
    private readonly WalhallaSqlDbConnection _connection;
    private bool _disposed;

    /// <summary>Required. The destination table name (unquoted).</summary>
    public string DestinationTableName { get; set; } = string.Empty;

    /// <summary>
    /// Number of rows per internal batch flush.
    /// Larger values reduce per-transaction overhead; smaller values reduce memory pressure.
    /// Default is 500.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    public WalhallaSqlBulkCopy(WalhallaSqlDbConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>Bulk-inserts all rows from <paramref name="table"/>.</summary>
    /// <returns>Total number of rows inserted.</returns>
    public int WriteToServer(DataTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        ThrowIfInvalid();

        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();

        return WriteRows(columns, table.Rows.Cast<DataRow>().Select(row =>
            columns.Select((_, i) => row[i]).ToArray()));
    }

    /// <summary>Bulk-inserts rows from an enumerable of dictionaries.</summary>
    /// <returns>Total number of rows inserted.</returns>
    public int WriteToServer(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ThrowIfInvalid();

        string[]? columns = null;
        var batches = new List<SqlClientCommand>();
        var total = 0;

        foreach (var row in rows)
        {
            columns ??= row.Keys.ToArray();
            var values = columns.Select(col => row.TryGetValue(col, out var v) ? v : DBNull.Value).ToArray();
            batches.Add(BuildInsertCommand(columns, values));

            if (batches.Count >= BatchSize)
            {
                _connection.ExecuteBatch(batches);
                total += batches.Count;
                batches.Clear();
            }
        }

        if (batches.Count > 0)
        {
            _connection.ExecuteBatch(batches);
            total += batches.Count;
        }

        return total;
    }

    /// <summary>Bulk-inserts all rows from <paramref name="reader"/>.</summary>
    /// <returns>Total number of rows inserted.</returns>
    public int WriteToServer(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ThrowIfInvalid();

        var fieldCount = reader.FieldCount;
        var columns = Enumerable.Range(0, fieldCount).Select(reader.GetName).ToArray();
        var buffer = new object[fieldCount];

        return WriteRows(columns, EnumerateReaderRows(reader, buffer));
    }

    private static IEnumerable<object[]> EnumerateReaderRows(DbDataReader reader, object[] buffer)
    {
        while (reader.Read())
        {
            reader.GetValues(buffer);
            var snapshot = (object[])buffer.Clone();
            yield return snapshot;
        }
    }

    private int WriteRows(string[] columns, IEnumerable<object?[]> rows)
    {
        var batches = new List<SqlClientCommand>(BatchSize);
        var total = 0;

        foreach (var values in rows)
        {
            batches.Add(BuildInsertCommand(columns, values));

            if (batches.Count >= BatchSize)
            {
                _connection.ExecuteBatch(batches);
                total += batches.Count;
                batches.Clear();
            }
        }

        if (batches.Count > 0)
        {
            _connection.ExecuteBatch(batches);
            total += batches.Count;
        }

        return total;
    }

    private SqlClientCommand BuildInsertCommand(string[] columns, object?[] values)
    {
        var sb = new StringBuilder();
        sb.Append("INSERT INTO ");
        AppendIdentifier(sb, DestinationTableName);
        sb.Append(" (");

        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            AppendIdentifier(sb, columns[i]);
        }

        sb.Append(") VALUES (");
        var parameters = new SqlClientParameter[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var paramName = $"p_{i}";
            sb.Append('@').Append(paramName);
            parameters[i] = new SqlClientParameter(paramName, NormalizeValue(values[i]));
        }

        sb.Append(')');

        return new SqlClientCommand(sb.ToString(), Parameters: parameters);
    }

    private static object? NormalizeValue(object? value) =>
        value is DBNull ? null : value;

    private static void AppendIdentifier(StringBuilder sb, string name) => sb.Append(name);

    private void ThrowIfInvalid()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(DestinationTableName))
            throw new InvalidOperationException("DestinationTableName must be set before calling WriteToServer.");
    }

    public void Dispose() => _disposed = true;
}
