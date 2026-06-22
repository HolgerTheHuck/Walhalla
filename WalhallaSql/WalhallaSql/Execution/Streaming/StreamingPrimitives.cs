using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalhallaSql.Execution.Streaming;

/// <summary>
/// Leichtgewichtige Zeilen-Implemetierung für den Streaming-Pfad.
/// Der Operator liefert einen Verweis auf einen gepufferten Wertearray;
/// der Consumer muss die Werte kopieren, wenn er sie über MoveNext() hinaus benötigt.
/// </summary>
internal readonly struct StreamingRow
{
    public object?[] Values { get; }

    public int Count => Values.Length;

    public StreamingRow(object?[] values)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }
}

/// <summary>
/// Kontext, der allen Streaming-Operatoren eines Planes gemeinsam ist.
/// </summary>
internal sealed class StreamingContext
{
    public WalhallaEngine Engine { get; }
    public Storage.TableStore Store { get; }
    public CompiledPlan Plan { get; }
    public Sql.SqlSelectStatement Select { get; }
    public object?[] Parameters { get; }
    public WalhallaOptions Options { get; }
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Optionaler MVCC-Lese-Snapshot. Wenn gesetzt, verwendet der Scan-Operator
    /// snapshot-konsistente Scans statt lock-basierter Iteration.
    /// </summary>
    public Walhalla.Storage.Contract.IReadSnapshot? Snapshot { get; }

    public StreamingContext(
        WalhallaEngine engine,
        Storage.TableStore store,
        CompiledPlan plan,
        Sql.SqlSelectStatement select,
        object?[] parameters,
        WalhallaOptions options,
        CancellationToken cancellationToken,
        Walhalla.Storage.Contract.IReadSnapshot? snapshot = null)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Select = select ?? throw new ArgumentNullException(nameof(select));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        CancellationToken = cancellationToken;
        Snapshot = snapshot;
    }
}

/// <summary>
/// Operator im pull-basierten Streaming-Ausführungsmodell.
/// Die synchronen <see cref="Execute"/>-Implementierungen bilden die Pipeline;
/// der asynchrone Wrapper dient ausschließlich der öffentlichen API-Oberfläche.
/// </summary>
internal interface IStreamingOperator
{
    IEnumerable<StreamingRow> Execute(StreamingContext context);

    IAsyncEnumerable<StreamingRow> ExecuteAsync(StreamingContext context, CancellationToken cancellationToken)
        => new SyncEnumerableAsyncAdapter<StreamingRow>(Execute(context));
}
