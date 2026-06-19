using System.Collections.Generic;
using System.Linq;

namespace WalhallaSql;

public sealed class WalhallaResultSet
{
    public IReadOnlyList<WalhallaRow> Rows { get; }
    public IReadOnlyList<string> ColumnNames { get; }
    public int AffectedRows { get; }
    private IReadOnlyDictionary<string, object?>? _outputParameters;
    public IReadOnlyDictionary<string, object?> OutputParameters
    {
        get => _outputParameters ??= new Dictionary<string, object?>();
        init => _outputParameters = value;
    }

    internal WalhallaResultSet(IReadOnlyList<WalhallaRow> rows, IReadOnlyList<string> columnNames, int affectedRows = 0)
    {
        Rows = rows;
        ColumnNames = columnNames;
        AffectedRows = affectedRows;
    }

    public WalhallaResultSet WithOutputParameters(IReadOnlyDictionary<string, object?> outputs)
    {
        return new WalhallaResultSet(Rows, ColumnNames, AffectedRows)
        {
            OutputParameters = outputs
        };
    }

    public static WalhallaResultSet Empty(string[] columnNames) =>
        new(System.Array.Empty<WalhallaRow>(), columnNames);

    public static WalhallaResultSet Affected(int count) =>
        new(System.Array.Empty<WalhallaRow>(), System.Array.Empty<string>(), count);

    public static WalhallaResultSet Single(string[] columnNames, WalhallaRow row) =>
        new(new SingleRowList(row), columnNames);

    private readonly struct SingleRowList : IReadOnlyList<WalhallaRow>
    {
        private readonly WalhallaRow _row;
        public SingleRowList(WalhallaRow row) => _row = row;
        public WalhallaRow this[int index] => index == 0 ? _row : throw new System.ArgumentOutOfRangeException(nameof(index));
        public int Count => 1;
        public IEnumerator<WalhallaRow> GetEnumerator()
        {
            yield return _row;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Build a result set from a sequence of dictionary-shaped rows
    /// (e.g. those returned by <see cref="SqlNativeProcedureContext.Query"/>).
    /// Column order is taken from the first row.
    /// </summary>
    public static WalhallaResultSet FromRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows == null || rows.Count == 0)
            return new WalhallaResultSet(System.Array.Empty<WalhallaRow>(), System.Array.Empty<string>());

        var names = rows[0].Keys.ToArray();
        var schema = new ColumnSchema(names);
        var built = new WalhallaRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var src = rows[i];
            var values = new object?[names.Length];
            for (var c = 0; c < names.Length; c++)
                src.TryGetValue(names[c], out values[c]);
            built[i] = new WalhallaRow(schema, values);
        }
        return new WalhallaResultSet(built, names);
    }
}
