using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WalhallaSql.AdoNet;

public sealed class WalhallaSqlDbDataReader : DbDataReader
{
    private readonly List<IReadOnlyDictionary<string, object?>> _rows;
    private readonly IEnumerator<IReadOnlyDictionary<string, object?>>? _streamingEnumerator;
    private readonly List<string> _columns;
    private readonly List<Type> _columnTypes;
    private readonly List<string?> _columnValueKeys;
    private readonly ScalarResultData? _scalarData;
    private IReadOnlyDictionary<string, object?>? _currentRow;
    private int _position = -1;
    private bool _isClosed;
    private int _recordsAffected = -1;
    private Action? _closeConnectionCallback;

    // Multi-resultset support: holds all result sets and current index.
    private readonly IReadOnlyList<SqlExecutionResult> _resultSets;
    private readonly IReadOnlyList<string>? _projectedColumns;
    private int _resultSetIndex = -1;

    internal WalhallaSqlDbDataReader(ScalarResultData scalarData)
    {
        _scalarData = scalarData;
        _rows = new List<IReadOnlyDictionary<string, object?>>();
        _streamingEnumerator = null;
        _columns = new List<string> { NormalizeDisplayColumnName(scalarData.ColumnName) };
        _columnTypes = new List<Type> { scalarData.ColumnType };
        _columnValueKeys = new List<string?> { null };
        _resultSets = Array.Empty<SqlExecutionResult>();
        _projectedColumns = null;
    }

    public WalhallaSqlDbDataReader(
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows,
        IReadOnlyList<string>? projectedColumns = null)
        : this(rows, projectedColumns, materializeEagerly: true)
    {
    }

    public WalhallaSqlDbDataReader(
        IEnumerable<IReadOnlyDictionary<string, object?>>? rows,
        IReadOnlyList<string>? projectedColumns = null,
        bool materializeEagerly = false)
    {
        if (materializeEagerly)
        {
            // Avoid copying the list when the source is already a List<> (e.g. from TryExecuteDirectProjectedQuerySelect).
            _rows = rows is List<IReadOnlyDictionary<string, object?>> existingList
                ? existingList
                : rows?.ToList() ?? new List<IReadOnlyDictionary<string, object?>>();
            _streamingEnumerator = null;
        }
        else
        {
            _rows = new List<IReadOnlyDictionary<string, object?>>();
            _streamingEnumerator = rows?.GetEnumerator();
        }

        _columns = projectedColumns?.Select(NormalizeDisplayColumnName).ToList() ?? new List<string>();
        _columnTypes = _columns.Select(_ => typeof(object)).ToList();
        _columnValueKeys = projectedColumns?.Select(static column => (string?)column).ToList()
            ?? _columns.Select(_ => (string?)null).ToList();

        if (_rows.Count > 0)
            EnsureSchemaFromRow(_rows[0]);

        for (var i = 1; i < _rows.Count; i++)
            UpdateColumnTypes(_rows[i]);

        _resultSets = Array.Empty<SqlExecutionResult>();
        _projectedColumns = projectedColumns;
    }

    /// <summary>
    /// Konstruktor für Multi-Resultset-Batches (z. B. Dapper QueryMultiple).
    /// </summary>
    public WalhallaSqlDbDataReader(IReadOnlyList<SqlExecutionResult> resultSets, IReadOnlyList<string>? projectedColumns = null)
    {
        _resultSets = resultSets ?? throw new ArgumentNullException(nameof(resultSets));
        _projectedColumns = projectedColumns;

        // Start with the first result set.
        _rows = new List<IReadOnlyDictionary<string, object?>>();
        _streamingEnumerator = null;
        _columns = new List<string>();
        _columnTypes = new List<Type>();
        _columnValueKeys = new List<string?>();
        _scalarData = null;

        MoveToResultSet(0);
    }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int Depth => 0;

    public override int FieldCount
    {
        get
        {
            EnsureSchemaInitialized();
            return _columns.Count;
        }
    }

    public override bool HasRows
    {
        get
        {
            if (_scalarData != null)
                return _scalarData.Values.Count > 0;
            EnsureSchemaInitialized();
            if (_rows.Count == 0 && _currentRow == null && _streamingEnumerator != null)
                TryReadNextStreamingRow();
            return _rows.Count > 0 || _currentRow != null;
        }
    }

    public override bool IsClosed => _isClosed;

    public override int RecordsAffected => _recordsAffected;

    internal void SetRecordsAffected(int value) => _recordsAffected = value;

    internal void SetCloseConnectionCallback(Action callback) => _closeConnectionCallback = callback;

    public override bool GetBoolean(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is bool b) return b;
        if (v is int i) return i != 0;
        if (v is long l) return l != 0;
        return Convert.ToBoolean(v, CultureInfo.InvariantCulture);
    }

    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = GetBinaryValue(ordinal);
        var available = Math.Max(0, bytes.Length - (int)dataOffset);
        var toCopy = Math.Min(available, length);

        if (buffer != null && toCopy > 0)
            Array.Copy(bytes, dataOffset, buffer, bufferOffset, toCopy);

        return toCopy;
    }

    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal), CultureInfo.InvariantCulture);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var chars = (GetString(ordinal) ?? string.Empty).ToCharArray();
        var available = Math.Max(0, chars.Length - (int)dataOffset);
        var toCopy = Math.Min(available, length);

        if (buffer != null && toCopy > 0)
            Array.Copy(chars, dataOffset, buffer, bufferOffset, toCopy);

        return toCopy;
    }

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override DateTime GetDateTime(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is DateTime dt) return dt;
        return Convert.ToDateTime(v, CultureInfo.InvariantCulture);
    }

    public override decimal GetDecimal(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is decimal dec) return dec;
        if (v is long l) return l;
        if (v is int i) return i;
        if (v is double d) return (decimal)d;
        return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
    }

    public override double GetDouble(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is double d) return d;
        if (v is float f) return f;
        if (v is long l) return l;
        if (v is int i) return i;
        if (v is decimal dec) return (double)dec;
        return Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    public override Type GetFieldType(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _columnTypes.Count)
            throw new IndexOutOfRangeException($"Invalid field ordinal '{ordinal}'.");

        return _columnTypes[ordinal];
    }

    public override float GetFloat(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is float f) return f;
        if (v is double d) return (float)d;
        return Convert.ToSingle(v, CultureInfo.InvariantCulture);
    }

    public override Guid GetGuid(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            _ => throw new InvalidCastException("Value is not a GUID.")
        };
    }

    public override short GetInt16(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is short s) return s;
        if (v is int i) return (short)i;
        if (v is long l) return (short)l;
        return Convert.ToInt16(v, CultureInfo.InvariantCulture);
    }

    public override int GetInt32(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is short s) return s;
        return Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }

    public override long GetInt64(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is long l) return l;
        if (v is int i) return i;
        if (v is short s) return s;
        return Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    public override string GetName(int ordinal) => _columns[ordinal];

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            if (string.Equals(_columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new IndexOutOfRangeException($"Unknown column '{name}'.");
    }

    public override string GetString(int ordinal)
    {
        var v = GetRawValue(ordinal);
        if (v is string str) return str;
        if (v is DBNull || v == null) return string.Empty;
        return Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public override object GetValue(int ordinal)
    {
        var raw = GetRawValue(ordinal);
        return raw is WalhallaSql.PendingBlobValue pending ? pending.ToArray() : raw;
    }

    private object GetRawValue(int ordinal)
    {
        if (_scalarData != null)
        {
            if (ordinal != 0 || _position < 0 || _position >= _scalarData.Values.Count)
                throw new InvalidOperationException("Reader is not positioned on a row.");
            return _scalarData.Values[_position] ?? DBNull.Value;
        }

        var row = _streamingEnumerator != null
            ? _currentRow
            : (_position >= 0 && _position < _rows.Count ? _rows[_position] : null);

        if (row == null)
            throw new InvalidOperationException("Reader is not positioned on a row.");

        if (row is WalhallaRow walhallaRow)
            return walhallaRow.GetValue(ordinal) ?? DBNull.Value;

        var mappedKey = ordinal >= 0 && ordinal < _columnValueKeys.Count ? _columnValueKeys[ordinal] : null;

        if (mappedKey != null && row.TryGetValue(mappedKey, out var mappedValue))
            return mappedValue ?? DBNull.Value;

        var name = _columns[ordinal];

        if (TryResolveRowValue(row, name, out var value))
            return value ?? DBNull.Value;

        return DBNull.Value;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value == DBNull.Value || value == null)
        {
            if (default(T) == null)
                return default!;

            throw new InvalidCastException($"Column '{GetName(ordinal)}' contains NULL and cannot be converted to '{typeof(T).Name}'.");
        }

        if (value is T typed)
            return typed;

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType.IsEnum)
        {
            if (value is string enumText)
                return (T)Enum.Parse(targetType, enumText, ignoreCase: true);

            var enumUnderlyingType = Enum.GetUnderlyingType(targetType);
            var enumUnderlyingValue = value.GetType().IsEnum
                ? Convert.ChangeType(value, enumUnderlyingType, CultureInfo.InvariantCulture)
                : Convert.ChangeType(value, enumUnderlyingType, CultureInfo.InvariantCulture);

            return (T)Enum.ToObject(targetType, enumUnderlyingValue!);
        }

        if (targetType == typeof(byte[]))
            return (T)(object)GetBinaryValue(ordinal);

        if (targetType == typeof(Stream) || targetType == typeof(MemoryStream))
            return (T)(object)GetStream(ordinal);

        if (targetType == typeof(Guid))
        {
            if (value is string guidText)
                return (T)(object)Guid.Parse(guidText);

            throw new InvalidCastException($"Value in column '{GetName(ordinal)}' cannot be converted to Guid.");
        }

        if (targetType == typeof(DateOnly))
        {
            if (value is DateOnly dateOnly)
                return (T)(object)dateOnly;

            if (value is DateTime dateTime)
                return (T)(object)DateOnly.FromDateTime(dateTime);

            if (value is string dateText)
                return (T)(object)DateOnly.Parse(dateText, CultureInfo.InvariantCulture);

            throw new InvalidCastException($"Value in column '{GetName(ordinal)}' cannot be converted to DateOnly.");
        }

        if (targetType == typeof(TimeOnly))
        {
            if (value is TimeOnly timeOnly)
                return (T)(object)timeOnly;

            if (value is DateTime dateTime)
                return (T)(object)TimeOnly.FromDateTime(dateTime);

            if (value is TimeSpan timeSpan)
                return (T)(object)TimeOnly.FromTimeSpan(timeSpan);

            if (value is string timeText)
                return (T)(object)TimeOnly.Parse(timeText, CultureInfo.InvariantCulture);

            throw new InvalidCastException($"Value in column '{GetName(ordinal)}' cannot be converted to TimeOnly.");
        }

        if (targetType == typeof(TimeSpan))
        {
            if (value is TimeSpan timeSpan)
                return (T)(object)timeSpan;

            if (value is TimeOnly timeOnly)
                return (T)(object)timeOnly.ToTimeSpan();

            if (value is string timeSpanText)
                return (T)(object)TimeSpan.Parse(timeSpanText, CultureInfo.InvariantCulture);

            throw new InvalidCastException($"Value in column '{GetName(ordinal)}' cannot be converted to TimeSpan.");
        }

        if (targetType == typeof(DateTimeOffset))
        {
            if (value is DateTimeOffset dateTimeOffset)
                return (T)(object)dateTimeOffset;

            if (value is DateTime dateTime)
            {
                var normalizedDateTime = dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime;

                return (T)(object)new DateTimeOffset(normalizedDateTime);
            }

            if (value is string dateTimeOffsetText)
                return (T)(object)DateTimeOffset.Parse(dateTimeOffsetText, CultureInfo.InvariantCulture);

            throw new InvalidCastException($"Value in column '{GetName(ordinal)}' cannot be converted to DateTimeOffset.");
        }

        if (targetType == typeof(uint))
        {
            return (T)(object)unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        if (targetType == typeof(ulong))
        {
            if (value is decimal dec)
                return (T)(object)Convert.ToUInt64(dec, CultureInfo.InvariantCulture);
            return (T)(object)unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    public override Stream GetStream(int ordinal)
    {
        var value = GetRawValue(ordinal);
        return value switch
        {
            DBNull => throw new InvalidCastException($"Column '{GetName(ordinal)}' is NULL and cannot be read as stream."),
            WalhallaSql.PendingBlobValue pending => pending.OpenStream(),
            byte[] bytes => new MemoryStream(bytes, writable: false),
            string text when TryDecodeBase64(text, out var binary) => new MemoryStream(binary, writable: false),
            string text => new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false),
            _ => throw new InvalidCastException($"Column '{GetName(ordinal)}' cannot be read as stream.")
        };
    }

    public override TextReader GetTextReader(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            DBNull => TextReader.Null,
            string text => new StringReader(text),
            char[] chars => new StringReader(new string(chars)),
            byte[] bytes => new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true),
            _ => new StringReader(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    public override int GetValues(object[] values)
    {
        var take = Math.Min(values.Length, _columns.Count);
        for (var i = 0; i < take; i++)
            values[i] = GetValue(i);

        return take;
    }

    public override bool IsDBNull(int ordinal)
    {
        var value = GetValue(ordinal);
        return value == DBNull.Value || value == null;
    }

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsDBNull(ordinal));
    }

    public override DataTable GetSchemaTable()
    {
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add("ColumnName", typeof(string));
        schema.Columns.Add("ColumnOrdinal", typeof(int));
        schema.Columns.Add("DataType", typeof(Type));
        schema.Columns.Add("DataTypeName", typeof(string));
        schema.Columns.Add("AllowDBNull", typeof(bool));
        schema.Columns.Add("ColumnSize", typeof(int));
        schema.Columns.Add("NumericPrecision", typeof(int));
        schema.Columns.Add("NumericScale", typeof(int));
        schema.Columns.Add("IsUnique", typeof(bool));
        schema.Columns.Add("IsKey", typeof(bool));
        schema.Columns.Add("IsAutoIncrement", typeof(bool));
        schema.Columns.Add("IsReadOnly", typeof(bool));
        schema.Columns.Add("IsRowVersion", typeof(bool));
        schema.Columns.Add("IsLong", typeof(bool));
        schema.Columns.Add("BaseTableName", typeof(string));
        schema.Columns.Add("BaseColumnName", typeof(string));

        for (var ordinal = 0; ordinal < _columns.Count; ordinal++)
        {
            var columnName = _columns[ordinal];
            var dataType = _columnTypes[ordinal];
            var row = schema.NewRow();
            row["ColumnName"] = columnName;
            row["ColumnOrdinal"] = ordinal;
            row["DataType"] = dataType;
            row["DataTypeName"] = dataType.Name;
            row["AllowDBNull"] = true;
            row["ColumnSize"] = dataType == typeof(string) ? int.MaxValue : DBNull.Value;
            row["NumericPrecision"] = DBNull.Value;
            row["NumericScale"] = DBNull.Value;
            row["IsUnique"] = false;
            row["IsKey"] = false;
            row["IsAutoIncrement"] = false;
            row["IsReadOnly"] = false;
            row["IsRowVersion"] = false;
            row["IsLong"] = dataType == typeof(byte[]);
            row["BaseTableName"] = DBNull.Value;
            row["BaseColumnName"] = columnName;
            schema.Rows.Add(row);
        }

        return schema;
    }

    public override bool NextResult()
    {
        if (_resultSets.Count == 0)
            return false;

        var nextIndex = _resultSetIndex + 1;
        if (nextIndex >= _resultSets.Count)
            return false;

        MoveToResultSet(nextIndex);
        return true;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    private void MoveToResultSet(int index)
    {
        _resultSetIndex = index;
        _position = -1;
        _currentRow = null;
        _rows.Clear();
        _columns.Clear();
        _columnTypes.Clear();
        _columnValueKeys.Clear();
        _recordsAffected = -1;

        var resultSet = _resultSets[index];
        var sourceRows = resultSet.Rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>();
        _rows.AddRange(sourceRows);

        _columns.AddRange(_projectedColumns?.Select(NormalizeDisplayColumnName).ToList() ?? new List<string>());
        _columnTypes.AddRange(_columns.Select(_ => typeof(object)));
        _columnValueKeys.AddRange(_projectedColumns?.Select(static column => (string?)column).ToList()
            ?? _columns.Select(_ => (string?)null).ToList());

        if (_rows.Count > 0)
        {
            EnsureSchemaFromRow(_rows[0]);
            for (var i = 1; i < _rows.Count; i++)
                UpdateColumnTypes(_rows[i]);
        }

        if (resultSet.AffectedRows >= 0)
            _recordsAffected = resultSet.AffectedRows;
    }

    public override bool Read()
    {
        if (_isClosed)
            return false;

        if (_scalarData != null)
        {
            var next = _position + 1;
            if (next >= _scalarData.Values.Count)
                return false;
            _position = next;
            return true;
        }

        if (_streamingEnumerator != null)
        {
            // Streaming mode: only hold the current row.
            // Wenn FieldCount/HasRows bereits eine Zeile vorgelesen hat
            // (position == -1 und currentRow != null), diese direkt liefern,
            // sonst erst zur naechsten Zeile wechseln.
            if (_position == -1 && _currentRow != null)
            {
                _position = 0;
                return true;
            }

            if (!TryReadNextStreamingRow())
                return false;
            _position = 0;
            return true;
        }

        var nextRow = _position + 1;
        if (nextRow >= _rows.Count)
            return false;

        _position = nextRow;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetFieldValue<T>(ordinal));
    }

    public override IEnumerator GetEnumerator()
    {
        return _rows.GetEnumerator();
    }

    public override void Close()
    {
        _streamingEnumerator?.Dispose();
        _isClosed = true;
        var callback = Interlocked.Exchange(ref _closeConnectionCallback, null);
        callback?.Invoke();
    }

    private bool TryReadNextStreamingRow()
    {
        if (_streamingEnumerator == null)
            return false;

        if (!_streamingEnumerator.MoveNext())
            return false;

        _currentRow = _streamingEnumerator.Current;
        if (_columns.Count == 0)
            EnsureSchemaFromRow(_currentRow);
        return true;
    }

    private void EnsureSchemaInitialized()
    {
        if (_columns.Count > 0)
            return;

        if (_rows.Count > 0)
        {
            EnsureSchemaFromRow(_rows[0]);
            return;
        }

        TryReadNextStreamingRow();
    }

    private void EnsureSchemaFromRow(IReadOnlyDictionary<string, object?> row)
    {
        if (_columns.Count == 0)
        {
            foreach (var key in row.Keys)
            {
                _columns.Add(key);
                _columnTypes.Add(typeof(object));
                _columnValueKeys.Add(key);
            }
        }

        UpdateColumnValueKeys(row);

        UpdateColumnTypes(row);
    }

    private void UpdateColumnTypes(IReadOnlyDictionary<string, object?> row)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            if (_columnTypes[i] != typeof(object))
                continue;

            var mappedKey = _columnValueKeys[i] ?? _columns[i];
            if (!row.TryGetValue(mappedKey, out var sampleValue) || sampleValue == null)
                continue;

            _columnTypes[i] = sampleValue.GetType();
        }
    }

    private void UpdateColumnValueKeys(IReadOnlyDictionary<string, object?> row)
    {
        if (_columns.Count == 0)
            return;

        var rowKeys = row.Keys.ToList();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _columnValueKeys)
        {
            if (key != null && row.ContainsKey(key))
                usedKeys.Add(key);
        }

        for (var i = 0; i < _columns.Count; i++)
        {
            var mappedKey = _columnValueKeys[i];
            if (mappedKey != null && row.ContainsKey(mappedKey))
                continue;

            if (TryResolveColumnValueKey(rowKeys, usedKeys, _columns[i], out var resolvedKey))
            {
                _columnValueKeys[i] = resolvedKey;
                usedKeys.Add(resolvedKey);
            }
        }
    }

    private static bool TryResolveColumnValueKey(
        IReadOnlyList<string> rowKeys,
        ISet<string> usedKeys,
        string columnName,
        out string resolvedKey)
    {
        foreach (var key in rowKeys)
        {
            if (usedKeys.Contains(key))
                continue;

            if (string.Equals(key, columnName, StringComparison.Ordinal))
            {
                resolvedKey = key;
                return true;
            }
        }

        foreach (var key in rowKeys)
        {
            if (usedKeys.Contains(key))
                continue;

            if (string.Equals(key, columnName, StringComparison.OrdinalIgnoreCase))
            {
                resolvedKey = key;
                return true;
            }
        }

        var suffix = columnName;
        var dot = columnName.LastIndexOf('.');
        if (dot > 0 && dot + 1 < columnName.Length)
            suffix = columnName[(dot + 1)..];

        foreach (var key in rowKeys)
        {
            if (usedKeys.Contains(key))
                continue;

            var keyDot = key.LastIndexOf('.');
            var keySuffix = keyDot > 0 && keyDot + 1 < key.Length ? key[(keyDot + 1)..] : key;
            if (string.Equals(keySuffix, suffix, StringComparison.OrdinalIgnoreCase))
            {
                resolvedKey = key;
                return true;
            }
        }

        resolvedKey = string.Empty;
        return false;
    }

    private static bool TryResolveRowValue(IReadOnlyDictionary<string, object?> row, string columnName, out object? value)
    {
        if (row.TryGetValue(columnName, out value))
            return true;

        foreach (var pair in row)
        {
            if (string.Equals(pair.Key, columnName, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        var dot = columnName.LastIndexOf('.');
        if (dot > 0 && dot + 1 < columnName.Length)
        {
            var suffix = columnName[(dot + 1)..];
            if (row.TryGetValue(suffix, out value))
                return true;

            foreach (var pair in row)
            {
                if (string.Equals(pair.Key, suffix, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        foreach (var pair in row)
        {
            var keyDot = pair.Key.LastIndexOf('.');
            var keySuffix = keyDot > 0 && keyDot + 1 < pair.Key.Length ? pair.Key[(keyDot + 1)..] : pair.Key;
            if (string.Equals(keySuffix, columnName, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static readonly char[] ColumnNameTrimChars = ['[', ']', '"', '`'];

    private static string NormalizeDisplayColumnName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return columnName;

        // Qualifizierte Spaltennamen (z.B. "c.Id") unverändert lassen,
        // damit JOIN-Queries keine Spalten-Duplikate produzieren.
        return columnName.Trim(ColumnNameTrimChars);
    }

    private byte[] GetBinaryValue(int ordinal)
    {
        var value = GetRawValue(ordinal);

        return value switch
        {
            DBNull => Array.Empty<byte>(),
            WalhallaSql.PendingBlobValue pending => pending.ToArray(),
            byte[] bytes => bytes,
            string text when TryDecodeBase64(text, out var decoded) => decoded,
            _ => throw new InvalidCastException($"Column '{GetName(ordinal)}' cannot be converted to byte[].")
        };
    }

    private static bool TryDecodeBase64(string text, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

}
