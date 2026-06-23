using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WalhallaSql;

/// <summary>
/// Ergonomischer Builder für tabellarische Ergebnisse innerhalb von C#-Stored-Procedures.
/// Kann sowohl materialisierte <see cref="WalhallaResultSet"/>- als auch
/// Streaming-Ergebnisse (<see cref="WalhallaStreamResult"/>) erzeugen.
/// </summary>
public sealed class WalhallaResultSetBuilder
{
    private readonly ColumnSchema _schema;
    private readonly List<WalhallaRow> _rows = new();

    private WalhallaResultSetBuilder(ColumnSchema schema)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public static WalhallaResultSetBuilder Create(params string[] columnNames)
    {
        if (columnNames == null) throw new ArgumentNullException(nameof(columnNames));
        return new WalhallaResultSetBuilder(new ColumnSchema(columnNames));
    }

    public static WalhallaResultSetBuilder Create(IEnumerable<string> columnNames)
    {
        if (columnNames == null) throw new ArgumentNullException(nameof(columnNames));
        return new WalhallaResultSetBuilder(new ColumnSchema(columnNames.ToArray()));
    }

    /// <summary>
    /// Fügt eine Zeile mit positionsbasierten Werten hinzu.
    /// </summary>
    public WalhallaResultSetBuilder AddRow(params object?[] values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        if (values.Length != _schema.Count)
            throw new ArgumentException($"Expected {_schema.Count} values, got {values.Length}.", nameof(values));

        var copy = new object?[values.Length];
        values.CopyTo(copy, 0);
        _rows.Add(new WalhallaRow(_schema, copy));
        return this;
    }

    /// <summary>
    /// Fügt eine Zeile über namensbasierte Wertzuweisung hinzu.
    /// </summary>
    public WalhallaResultSetBuilder AddRow(Action<WalhallaRowBuilder> configure)
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        var builder = new WalhallaRowBuilder(_schema);
        configure(builder);
        _rows.Add(new WalhallaRow(_schema, builder.Values));
        return this;
    }

    /// <summary>
    /// Baut ein materialisiertes Ergebnis.
    /// </summary>
    public WalhallaResultSet ToResultSet()
        => new WalhallaResultSet(_rows, _schema.Names);

    /// <summary>
    /// Baut ein Streaming-Ergebnis aus den bisher hinzugefügten Zeilen.
    /// Nützlich, wenn der Builder als leichtgewichtiger Puffer verwendet wird.
    /// </summary>
    public WalhallaStreamResult ToStreamResult()
    {
        var columnTypes = Array.ConvertAll(_schema.Names, _ => typeof(object));
        var enumerable = AsAsyncArrays(_rows, _schema);
        return WalhallaStreamResult.FromRowsAsync(_schema.Names, enumerable, columnTypes);
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> AsAsyncArrays(
        IReadOnlyList<WalhallaRow> rows,
        ColumnSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return rows[i];
        }
    }
}
