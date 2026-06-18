using System;
using System.Collections.Generic;
using System.Globalization;
using WalhallaSql.Sql;

namespace WalhallaSql;

public sealed class SqlNativeProcedureContext
{
    private readonly WalhallaEngine _engine;
    private readonly Dictionary<string, object?> _outputs = new(StringComparer.OrdinalIgnoreCase);

    internal SqlNativeProcedureContext(WalhallaEngine engine, IReadOnlyList<SqlExecArgument> arguments)
    {
        _engine = engine;
        Arguments = arguments;
    }

    public IReadOnlyList<SqlExecArgument> Arguments { get; }

    public IReadOnlyDictionary<string, object?> Outputs => _outputs;

    public void SetOutput(string name, object? value) => _outputs[name] = value;

    public SqlExecArgument? GetArgument(int index)
        => index >= 0 && index < Arguments.Count ? Arguments[index] : null;

    public SqlExecArgument? GetArgument(string name)
    {
        var normalized = name.TrimStart('@');
        foreach (var arg in Arguments)
        {
            if (arg.ParameterName != null &&
                arg.ParameterName.TrimStart('@').Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return arg;
        }
        return null;
    }

    public T? Get<T>(string name)
    {
        var arg = GetArgument(name);
        return arg is null ? default : ParseAs<T>(arg.ValueExpression);
    }

    public T? Get<T>(int index)
    {
        var arg = GetArgument(index);
        return arg is null ? default : ParseAs<T>(arg.ValueExpression);
    }

    public WalhallaResultSet Execute(string sql) => _engine.Execute(sql);

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql)
    {
        var result = Execute(sql);
        var rows = new List<IReadOnlyDictionary<string, object?>>(result.Rows.Count);
        foreach (var row in result.Rows)
            rows.Add(row);
        return rows;
    }

    private static T? ParseAs<T>(string valueExpression)
    {
        var trimmed = valueExpression.Trim();

        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return default;

        if (trimmed.StartsWith("'", StringComparison.Ordinal) && trimmed.EndsWith("'", StringComparison.Ordinal))
        {
            var unquoted = trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return (T?)Convert.ChangeType(unquoted, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(bool))
        {
            var boolVal = trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("1", StringComparison.OrdinalIgnoreCase);
            return (T)(object)boolVal;
        }

        if (targetType == typeof(Guid))
            return (T)(object)Guid.Parse(trimmed);

        return (T)Convert.ChangeType(trimmed, targetType, CultureInfo.InvariantCulture);
    }
}
