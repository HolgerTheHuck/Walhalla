using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WalhallaSql;
using WalhallaSql.Sql;
using WalhallaSql.Statistics;

namespace WalhallaSql.PgWire;

/// <summary>
/// Adapter that maps <see cref="IPgWireBackendConnection"/> directly to
/// <see cref="WalhallaEngine"/>. Standalone PgWire frontend for WalhallaSql.
/// </summary>
public sealed class WalhallaSqlPgWireBackend : IPgWireBackendConnection
{
    private readonly WalhallaEngine _engine;

    public WalhallaSqlPgWireBackend(WalhallaEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    internal WalhallaEngine GetEngine() => _engine;

    public string DatabaseName => "WalhallaSql";

    public IPgWireBackendCommand CreateCommand()
        => new WalhallaBackendCommand(_engine);

    public IPgWireBackendTransaction BeginTransaction()
    {
        var tx = _engine.BeginTransaction();
        return new WalhallaSqlTransactionAdapter(tx);
    }

    public IReadOnlyList<PgVirtualTableDefinition> DiscoverTables()
    {
        try
        {
            var tables = _engine.GetAllTables();
            var result = new List<PgVirtualTableDefinition>(tables.Count);
            foreach (var table in tables)
            {
                var columns = new List<PgVirtualColumnDefinition>(table.Columns.Count);
                foreach (var col in table.Columns)
                {
                    columns.Add(new PgVirtualColumnDefinition(
                        col.Name,
                        MapTypeToString(col.Type),
                        col.IsNullable,
                        col.IsPrimaryKey,
                        col.Collation));
                }
                result.Add(new PgVirtualTableDefinition(table.CollectionName, columns));
            }
            return result;
        }
        catch
        {
            return Array.Empty<PgVirtualTableDefinition>();
        }
    }

    public IReadOnlyList<PgVirtualRoutineDefinition> DiscoverRoutines()
    {
        try
        {
            var procedures = _engine.GetProcedures();
            var result = new List<PgVirtualRoutineDefinition>(procedures.Count);
            foreach (var proc in procedures)
            {
                var parameters = proc.Parameters
                    .Select(p => new PgVirtualRoutineParameter(
                        p.Name,
                        MapTypeToString(p.Type),
                        p.IsOutput ? "OUT" : "IN"))
                    .ToArray();

                result.Add(new PgVirtualRoutineDefinition(
                    proc.Name,
                    string.Equals(proc.Language, "csharp", StringComparison.OrdinalIgnoreCase) ? "PROCEDURE" : "PROCEDURE",
                    parameters));
            }
            return result;
        }
        catch
        {
            return Array.Empty<PgVirtualRoutineDefinition>();
        }
    }

    public IReadOnlyList<Dictionary<string, object?>> GetPgStatsRows()
    {
        try
        {
            var result = new List<Dictionary<string, object?>>();
            var tables = _engine.GetAllTables();
            foreach (var table in tables)
            {
                var stats = _engine.GetStatistics(table.CollectionName);
                if (stats is null) continue;
                foreach (var col in table.Columns)
                {
                    if (!stats.Columns.TryGetValue(col.Name, out var colStats)) continue;
                    result.Add(BuildPgStatsRow(table.CollectionName, col.Name, colStats));
                }
            }
            return result;
        }
        catch
        {
            return Array.Empty<Dictionary<string, object?>>();
        }
    }

    private static Dictionary<string, object?> BuildPgStatsRow(string tableName, string colName, ColumnStatistics colStats)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["schemaname"] = "public",
            ["tablename"] = tableName,
            ["attname"] = colName,
            ["inherited"] = false,
            ["null_frac"] = (float)colStats.NullFraction,
            ["avg_width"] = colStats.AverageWidth,
            ["n_distinct"] = (float)colStats.DistinctCount,
            ["most_common_vals"] = FormatPgObjectArray(colStats.MostCommonValues.Select(v => v.Value).ToArray()),
            ["most_common_freqs"] = colStats.MostCommonValues.Length > 0
                ? "{" + string.Join(",", colStats.MostCommonValues.Select(v => v.Frequency.ToString("0.0000", CultureInfo.InvariantCulture))) + "}"
                : null,
            ["histogram_bounds"] = FormatPgObjectArray(colStats.Histogram),
            ["correlation"] = null
        };
    }

    private static string? FormatPgObjectArray(object[] values)
    {
        if (values.Length == 0) return null;
        return "{" + string.Join(",", values.Select(v => v?.ToString() ?? "")) + "}";
    }

    public IReadOnlyList<(string Name, Type ClrType)>? TryDescribeQuery(string sql)
    {
        // Fast path: extract table name + column list from simple SELECT without executing.
        // Fall back to null for complex queries so Describe uses TryReadFields.
        var span = sql.AsSpan().TrimStart();
        if (span.Length < 20 || !span.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return null;

        span = span.Slice(6).TrimStart(); // skip "SELECT"

        // Find " FROM "
        var fromIdx = IndexOfKeyword(span, " FROM ");
        if (fromIdx < 0) return null;
        var colsSpan = span.Slice(0, fromIdx).Trim();
        span = span.Slice(fromIdx + "FROM ".Length); // skip "FROM "

        // Find " WHERE " or end
        var whereIdx = IndexOfKeyword(span, " WHERE ");
        var tableSpan = (whereIdx >= 0 ? span.Slice(0, whereIdx) : span).Trim();
        if (tableSpan.IsEmpty) return null;

        // Split "schema.table" or "table" or "table AS alias"
        var dotIdx = tableSpan.IndexOf('.');
        var namePart = dotIdx >= 0 ? tableSpan.Slice(dotIdx + 1) : tableSpan;
        var spaceIdx = namePart.IndexOf(' ');
        var tableName = (spaceIdx >= 0 ? namePart.Slice(0, spaceIdx) : namePart).Trim().ToString();

        // Look up table definition
        var tableDef = _engine.GetTableDefinition(tableName);
        if (tableDef == null) return null;

        // Parse projected columns
        var isStar = colsSpan.Length == 1 && colsSpan[0] == '*';
        if (isStar)
        {
            var result = new (string Name, Type ClrType)[tableDef.Columns.Count];
            for (int i = 0; i < tableDef.Columns.Count; i++)
                result[i] = (tableDef.Columns[i].Name, MapTypeToClr(tableDef.Columns[i].Type));
            return result;
        }

        // Parse comma-separated column names
        var names = new List<string>(4);
        var start = 0;
        for (int i = 0; i <= colsSpan.Length; i++)
        {
            if (i == colsSpan.Length || colsSpan[i] == ',')
            {
                var name = colsSpan.Slice(start, i - start).Trim();
                if (name.IsEmpty) return null;
                // Skip table prefix "t.col"
                var tblPrefix = name.IndexOf('.');
                names.Add(tblPrefix >= 0 ? name.Slice(tblPrefix + 1).Trim().ToString() : name.ToString());
                start = i + 1;
            }
        }

        var projected = new (string Name, Type ClrType)[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            var colDef = FindColumn(tableDef, names[i]);
            if (colDef == null) return null;
            projected[i] = (colDef.Name, MapTypeToClr(colDef.Type));
        }
        return projected;
    }

    private static SqlColumnDefinition? FindColumn(SqlTableDefinition table, string name)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return table.Columns[i];
        }
        return null;
    }

    private static int IndexOfKeyword(ReadOnlySpan<char> s, string keyword)
    {
        var search = keyword.Trim();
        var len = search.Length;
        for (int i = 0; i <= s.Length - len; i++)
        {
            if (i > 0 && !IsWs(s[i - 1]))
                continue;
            if (!s.Slice(i, len).Equals(search.AsSpan(), StringComparison.OrdinalIgnoreCase))
                continue;
            var end = i + len;
            if (end < s.Length && !IsWs(s[end]))
                continue;
            return i;
        }
        return -1;
    }

    private static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';

    public void Dispose() { }

    // ── Type mapping ────────────────────────────────────────────────────────

    public bool TryGetStoredHash(string username, out string storedHash)
    {
        if (_engine.AuthIdCatalog.TryGetRole(username, out var entry))
        {
            storedHash = entry.Rolpassword;
            return true;
        }
        storedHash = string.Empty;
        return false;
    }

    private static string MapTypeToString(SqlScalarType type) => type switch
    {
        SqlScalarType.Int32 => "integer",
        SqlScalarType.Int64 => "bigint",
        SqlScalarType.Double => "double precision",
        SqlScalarType.Decimal => "numeric",
        SqlScalarType.String => "text",
        SqlScalarType.Boolean => "boolean",
        SqlScalarType.DateTime => "timestamp without time zone",
        SqlScalarType.Binary => "bytea",
        SqlScalarType.Guid => "uuid",
        SqlScalarType.Json => "jsonb",
        _ => "text"
    };

    internal static Type MapTypeToClr(SqlScalarType type) => type switch
    {
        SqlScalarType.Int32 => typeof(int),
        SqlScalarType.Int64 => typeof(long),
        SqlScalarType.Double => typeof(double),
        SqlScalarType.Decimal => typeof(decimal),
        SqlScalarType.Boolean => typeof(bool),
        SqlScalarType.DateTime => typeof(DateTime),
        SqlScalarType.Binary => typeof(byte[]),
        SqlScalarType.Guid => typeof(Guid),
        _ => typeof(string)
    };

    // ── Nested adapters ─────────────────────────────────────────────────────

    private sealed class WalhallaBackendCommand : IPgWireBackendCommand
    {
        private readonly WalhallaEngine _engine;
        private WalhallaSqlTransaction? _tx;

        public WalhallaBackendCommand(WalhallaEngine engine)
        {
            _engine = engine;
        }

        public string CommandText { get; set; } = string.Empty;

        public IPgWireBackendTransaction? Transaction
        {
            get => null;
            set
            {
                _tx = (value as WalhallaSqlTransactionAdapter)?._inner;
            }
        }

        public IPgWireBackendReader ExecuteReader()
        {
            try
            {
                var streamResult = _tx != null
                    ? throw new WalhallaException("Streaming with transactions is not supported.")
                    : _engine.ExecuteStreaming(CommandText);
                return new WalhallaBackendReader(streamResult);
            }
            catch (WalhallaException)
            {
                var result = _tx != null
                    ? _engine.Execute(CommandText, _tx)
                    : _engine.Execute(CommandText);
                return new WalhallaBackendReader(result);
            }
        }

        public int ExecuteNonQuery()
        {
            var result = _tx != null
                ? _engine.Execute(CommandText, _tx)
                : _engine.Execute(CommandText);
            return result.AffectedRows;
        }

        public void Dispose() { }
    }

    internal sealed class WalhallaBackendReader : IPgWireBackendReader
    {
        private readonly WalhallaResultSet? _result;
        private readonly IEnumerator<IReadOnlyDictionary<string, object?>>? _streamingEnumerator;
        private IReadOnlyDictionary<string, object?> _currentRow = null!;
        private readonly string[] _columnNames;
        private readonly Type[] _fieldTypes;
        private int _rowIndex = -1;

        /// <summary>Materialized constructor — types inferred from first row.</summary>
        public WalhallaBackendReader(WalhallaResultSet result)
        {
            _result = result;
            _columnNames = result.ColumnNames.ToArray();
            _fieldTypes = new Type[_columnNames.Length];

            if (result.Rows.Count > 0)
            {
                var firstRow = result.Rows[0];
                for (var i = 0; i < _columnNames.Length; i++)
                {
                    var val = firstRow.TryGetValue(_columnNames[i], out var v) ? v : null;
                    _fieldTypes[i] = val?.GetType() ?? typeof(string);
                }
            }
            else
            {
                for (var i = 0; i < _fieldTypes.Length; i++)
                    _fieldTypes[i] = typeof(string);
            }
        }

        /// <summary>Streaming constructor — types come from table metadata.</summary>
        public WalhallaBackendReader(WalhallaStreamResult streamResult)
        {
            _result = null;
            _columnNames = streamResult.ColumnNames.ToArray();
            _fieldTypes = streamResult.ColumnTypes.ToArray();
            _streamingEnumerator = streamResult.EnumerateRows().GetEnumerator();
        }

        public int FieldCount => _columnNames.Length;

        public string GetName(int i) => _columnNames[i];

        public Type GetFieldType(int i) => _fieldTypes[i];

        public bool Read()
        {
            if (_streamingEnumerator != null)
            {
                if (!_streamingEnumerator.MoveNext())
                    return false;
                _currentRow = _streamingEnumerator.Current;
                _rowIndex++;
                return true;
            }

            _rowIndex++;
            return _rowIndex < _result!.Rows.Count;
        }

        public bool IsDBNull(int i)
        {
            if (_streamingEnumerator != null)
                return _currentRow.TryGetValue(_columnNames[i], out var v) && v is null;

            return _result!.Rows[_rowIndex].IsNull(i);
        }

        public object GetValue(int i)
        {
            if (_streamingEnumerator != null)
                return _currentRow.TryGetValue(_columnNames[i], out var v) ? (v ?? DBNull.Value) : DBNull.Value;

            return _result!.Rows[_rowIndex].GetValue(i) ?? DBNull.Value;
        }

        public void Dispose()
        {
            _streamingEnumerator?.Dispose();
        }
    }

    private sealed class WalhallaSqlTransactionAdapter : IPgWireBackendTransaction
    {
        internal readonly WalhallaSqlTransaction _inner;

        public WalhallaSqlTransactionAdapter(WalhallaSqlTransaction inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public void Commit() => _inner.Commit();
        public void Rollback() => _inner.Rollback();
        public void Dispose() => _inner.Dispose();
    }
}
