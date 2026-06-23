using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WalhallaSql.Parsing.Plw;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution.Plw;

/// <summary>
/// Fuehrt SQL-Fragmente aus einem PLW-Body ueber <see cref="WalhallaEngine.Execute"/> aus
/// und ersetzt vorher PLW-Variablen und Argument-Platzhalter durch SQL-Literale.
/// </summary>
internal sealed class PlwSqlExecutor
{
    private readonly WalhallaEngine _engine;
    private readonly PlwExpressionEvaluator _evaluator;

    public PlwSqlExecutor(WalhallaEngine engine, PlwExpressionEvaluator evaluator)
    {
        _engine = engine;
        _evaluator = evaluator;
    }

    public WalhallaResultSet Execute(PlwSqlFragment fragment, PlwEnvironment env)
    {
        var sql = BuildSql(fragment, env);
        return _engine.Execute(sql);
    }

    public WalhallaResultSet Execute(string sql, IReadOnlyList<object?>? positionalArguments = null)
    {
        if (positionalArguments != null && positionalArguments.Count > 0)
            sql = BindPositionalArguments(sql, positionalArguments);

        return _engine.Execute(sql);
    }

    public string BuildSql(PlwSqlFragment fragment, PlwEnvironment env)
    {
        var sql = SubstituteFragmentArguments(fragment.Text, fragment.Arguments, env);
        sql = SubstituteVariables(sql, env);
        return sql;
    }

    private string SubstituteFragmentArguments(string text, IReadOnlyList<PlwExpression> arguments, PlwEnvironment env)
    {
        var sb = new StringBuilder(text.Length + arguments.Count * 8);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                int end = text.IndexOf('}', i + 1);
                if (end > i && int.TryParse(text.AsSpan(i + 1, end - i - 1), out var idx) && idx < arguments.Count)
                {
                    var value = _evaluator.Evaluate(arguments[idx], env);
                    sb.Append(FormatSqlValue(value, null));
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    private string SubstituteVariables(string sql, PlwEnvironment env)
    {
        var sb = new StringBuilder(sql.Length);
        int i = 0;
        bool inString = false;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (c == '\'')
            {
                if (inString && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }

                inString = !inString;
                sb.Append(c);
                i++;
                continue;
            }

            if (!inString && IsIdentifierStart(c, allowAt: true))
            {
                int start = i;
                i++;
                while (i < sql.Length && IsIdentifierPart(sql[i], allowAt: true))
                    i++;

                var token = sql[start..i];
                var normalized = token.TrimStart('@');

                if (env.Contains(token) || env.Contains(normalized))
                {
                    var value = env.Get(token);
                    var typeName = env.TryGetTypeName(token) ?? env.TryGetTypeName(normalized);
                    var type = TryParseScalarType(typeName);
                    sb.Append(FormatSqlValue(value, type));
                    continue;
                }

                sb.Append(token);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string BindPositionalArguments(string sql, IReadOnlyList<object?> arguments)
    {
        // $1, $2, ... durch formatierte Literale ersetzen.
        return Regex.Replace(sql, @"\$(\d+)\b", match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out var index) || index < 1 || index > arguments.Count)
                return match.Value;
            return FormatSqlValue(arguments[index - 1], null);
        }, RegexOptions.CultureInvariant);
    }

    internal static string FormatSqlValue(object? value, SqlScalarType? type)
    {
        if (value == null)
            return "NULL";

        if (type == SqlScalarType.Boolean || value is bool)
            return value is true ? "true" : "false";

        if (value is DateTime dt)
            return "'" + dt.ToString("O", CultureInfo.InvariantCulture) + "'";

        if (value is DateOnly dateOnly)
            return "'" + dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";

        if (value is TimeSpan timeSpan)
            return "'" + timeSpan.ToString("c", CultureInfo.InvariantCulture) + "'";

        if (value is TimeOnly timeOnly)
            return "'" + timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'";

        if (value is Guid guid)
            return "'" + guid.ToString() + "'";

        if (value is string text)
            return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

        if (type.HasValue && IsNumericType(type.Value) || IsNumericObject(value))
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL";

        return "'" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    internal static SqlScalarType TryParseScalarType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return SqlScalarType.Unknown;

        var baseType = typeName.Trim();
        var parenIdx = baseType.IndexOf('(');
        if (parenIdx >= 0)
            baseType = baseType[..parenIdx].Trim();

        return baseType.ToUpperInvariant() switch
        {
            "INT" or "INTEGER" or "INT32" => SqlScalarType.Int32,
            "BIGINT" or "LONG" or "INT64" => SqlScalarType.Int64,
            "SMALLINT" or "INT16" => SqlScalarType.Int16,
            "FLOAT" or "REAL" or "DOUBLE" => SqlScalarType.Double,
            "DECIMAL" or "NUMERIC" or "MONEY" => SqlScalarType.Decimal,
            "VARCHAR" or "NVARCHAR" or "TEXT" or "STRING" or "CHAR" or "NCHAR" => SqlScalarType.String,
            "BIT" or "BOOL" or "BOOLEAN" => SqlScalarType.Boolean,
            "DATETIME" or "DATETIME2" or "TIMESTAMP" => SqlScalarType.DateTime,
            "DATE" => SqlScalarType.Date,
            "TIME" => SqlScalarType.Time,
            "UNIQUEIDENTIFIER" or "UUID" or "GUID" => SqlScalarType.Guid,
            "VARBINARY" or "BINARY" or "BLOB" => SqlScalarType.Binary,
            "JSON" => SqlScalarType.Json,
            _ => SqlScalarType.Unknown
        };
    }

    private static bool IsIdentifierStart(char c, bool allowAt)
        => char.IsLetter(c) || c == '_' || (allowAt && c == '@');

    private static bool IsIdentifierPart(char c, bool allowAt)
        => char.IsLetterOrDigit(c) || c == '_' || c == '$' || (allowAt && c == '@');

    private static bool IsNumericType(SqlScalarType type)
        => type is SqlScalarType.Int32 or SqlScalarType.Int64 or SqlScalarType.Int16
               or SqlScalarType.Double or SqlScalarType.Decimal;

    private static bool IsNumericObject(object value)
        => value is byte or sbyte or short or ushort or int or uint
               or long or ulong or float or double or decimal;
}
