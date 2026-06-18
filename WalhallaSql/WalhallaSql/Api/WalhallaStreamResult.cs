using System;
using System.Collections.Generic;

namespace WalhallaSql;

/// <summary>
/// Streaming result of a query. Rows are produced one-at-a-time from the
/// underlying storage without materializing the entire result set.
/// </summary>
public sealed class WalhallaStreamResult : IDisposable
{
    /// <summary>Column names in output order.</summary>
    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>CLR types for each column, derived from table metadata.</summary>
    public IReadOnlyList<Type> ColumnTypes { get; }

    internal ColumnSchema Schema { get; }

    /// <summary>
    /// Lazy row enumerator. Each yielded array is a new allocation (projected copy).
    /// The enumerator is valid only until Dispose is called — Dispose releases
    /// the underlying read lock and storage enumerator.
    /// </summary>
    internal IEnumerator<object?[]> RowEnumerator { get; }

    internal WalhallaStreamResult(
        IReadOnlyList<string> columnNames,
        IReadOnlyList<Type> columnTypes,
        ColumnSchema schema,
        IEnumerator<object?[]> rowEnumerator)
    {
        ColumnNames = columnNames ?? throw new ArgumentNullException(nameof(columnNames));
        ColumnTypes = columnTypes ?? throw new ArgumentNullException(nameof(columnTypes));
        Schema = schema;
        RowEnumerator = rowEnumerator ?? throw new ArgumentNullException(nameof(rowEnumerator));
    }

    /// <summary>
    /// Enumerates rows as dictionaries, compatible with the existing
    /// <c>IEnumerable&lt;IReadOnlyDictionary&lt;string, object?&gt;&gt;</c> contract.
    /// Each row is wrapped in a <see cref="WalhallaRow"/> struct.
    /// The underlying storage enumerator and read lock are released when this
    /// enumerator is disposed or when enumeration completes.
    /// </summary>
    public IEnumerable<IReadOnlyDictionary<string, object?>> EnumerateRows()
    {
        try
        {
            while (RowEnumerator.MoveNext())
                yield return new WalhallaRow(Schema, RowEnumerator.Current);
        }
        finally
        {
            RowEnumerator.Dispose();
        }
    }

    /// <summary>
    /// Releases the underlying storage enumerator and read lock.
    /// Safe to call even if the enumerator was already disposed via EnumerateRows().
    /// </summary>
    public void Dispose() => RowEnumerator.Dispose();
}
