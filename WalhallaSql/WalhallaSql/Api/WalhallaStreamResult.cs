using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>
    /// True when the result was produced without full materialization.
    /// A value of false indicates that an operator (e.g. ORDER BY or a Hash-Join)
    /// the result internally buffered.
    /// </summary>
    public bool IsFullyMaterialized { get; }

    internal ColumnSchema Schema { get; }

    /// <summary>
    /// Lazy row enumerator. Each yielded array is a new allocation (projected copy).
    /// The enumerator is valid only until Dispose is called — Dispose releases
    /// the underlying read lock and storage enumerator.
    /// </summary>
    internal IEnumerator<object?[]>? RowEnumerator { get; }

    /// <summary>
    /// Asynchronous row enumerable used by the new streaming pipeline.
    /// </summary>
    internal IAsyncEnumerable<object?[]>? RowEnumerable { get; }

    public WalhallaStreamResult(
        IReadOnlyList<string> columnNames,
        IReadOnlyList<Type> columnTypes,
        ColumnSchema schema,
        IEnumerator<object?[]> rowEnumerator,
        bool isFullyMaterialized = false)
    {
        ColumnNames = columnNames ?? throw new ArgumentNullException(nameof(columnNames));
        ColumnTypes = columnTypes ?? throw new ArgumentNullException(nameof(columnTypes));
        Schema = schema;
        RowEnumerator = rowEnumerator ?? throw new ArgumentNullException(nameof(rowEnumerator));
        IsFullyMaterialized = isFullyMaterialized;
    }

    public WalhallaStreamResult(
        IReadOnlyList<string> columnNames,
        IReadOnlyList<Type> columnTypes,
        ColumnSchema schema,
        IAsyncEnumerable<object?[]> rowEnumerable,
        bool isFullyMaterialized = false)
    {
        ColumnNames = columnNames ?? throw new ArgumentNullException(nameof(columnNames));
        ColumnTypes = columnTypes ?? throw new ArgumentNullException(nameof(columnTypes));
        Schema = schema;
        RowEnumerable = rowEnumerable ?? throw new ArgumentNullException(nameof(rowEnumerable));
        IsFullyMaterialized = isFullyMaterialized;
    }

    /// <summary>
    /// Baut ein Stream-Result aus einem fremden Zeilenstrom (z. B. zurückgegeben
    /// aus einer nativen Prozedur). Spaltentypen werden auf <see cref="object"/>
    /// gesetzt, wenn keine abgeleiteten Metadaten vorliegen.
    /// </summary>
    internal static WalhallaStreamResult FromRowsAsync(
        IReadOnlyList<string> columnNames,
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<Type>? columnTypes = null)
    {
        var schema = new ColumnSchema(columnNames.ToArray());
        var types = columnTypes ?? columnNames.Select(_ => typeof(object)).ToArray();

        return new WalhallaStreamResult(columnNames, types, schema, ProjectToArrays(rows, schema));
    }

    private static async IAsyncEnumerable<object?[]> ProjectToArrays(
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows,
        ColumnSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var names = schema.Names;
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            var values = new object?[names.Length];
            for (int i = 0; i < names.Length; i++)
                row.TryGetValue(names[i], out values[i]);
            yield return values;
        }
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
        var enumerator = RowEnumerator ?? throw new InvalidOperationException("Synchronous enumerator is not available.");
        try
        {
            while (enumerator.MoveNext())
                yield return new WalhallaRow(Schema, enumerator.Current);
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    /// <summary>
    /// Enumerates rows asynchronously as dictionaries.
    /// <para>
    /// Because the underlying storage layer holds thread-affine read locks,
    /// the asynchronous enumerator must be consumed on a single thread;
    /// do not use <c>ConfigureAwait(false)</c> while consuming this sequence.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> EnumerateRowsAsync(
        CancellationToken cancellationToken = default)
    {
        var source = RowEnumerable
            ?? (RowEnumerator != null
                ? new Execution.Streaming.SyncEnumerableAsyncAdapter<object?[]>(RowEnumerator)
                : throw new InvalidOperationException("No streaming enumerator is available."));

        return EnumerateRowsAsyncCore(source, Schema, cancellationToken);
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> EnumerateRowsAsyncCore(
        IAsyncEnumerable<object?[]> source,
        ColumnSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in source.WithCancellation(cancellationToken))
            yield return new WalhallaRow(schema, row);
    }

    /// <summary>
    /// Releases the underlying storage enumerator and read lock.
    /// Safe to call even if the enumerator was already disposed via EnumerateRows().
    /// </summary>
    public void Dispose()
    {
        RowEnumerator?.Dispose();
        // RowEnumerable is disposed by its own IAsyncEnumerator; nothing to do here.
    }
}
