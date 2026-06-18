using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WalhallaSql.AdoNet.SqlClient;

public static class SqlLiteralFormatter
{
    public static string RewriteParametersAsLiterals(SqlClientCommand command)
    {
        var sql = command.Sql;

        // CREATE [OR REPLACE] PROCEDURE enthält Parameterdeklarationen wie @id INT.
        // Diese sind keine SQL-Parameter, die aus dem Command ersetzt werden müssen.
        if (IsCreateProcedureStatement(sql))
            return sql;

        var parameterMap = BuildParameterMap(command.Parameters);
        return RewriteParameterTokensAsLiterals(sql, parameterMap);
    }

    private static IReadOnlyDictionary<string, object?> BuildParameterMap(IReadOnlyList<SqlClientParameter> parameters)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            var normalizedName = NormalizeParameterName(parameter.Name);
            if (!map.TryAdd(normalizedName, parameter.Value))
                throw new InvalidOperationException($"Duplicate SQL parameter name '{parameter.Name}' is not allowed.");
        }
        return map;
    }

    private static string RewriteParameterTokensAsLiterals(
        string sql,
        IReadOnlyDictionary<string, object?> parameterMap)
    {
        var builder = new StringBuilder(sql.Length + 32);
        var inSingleQuotedLiteral = false;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];

            if (current == '\'')
            {
                if (inSingleQuotedLiteral && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    builder.Append("''");
                    index++;
                    continue;
                }

                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuotedLiteral
                && (current == '@' || current == ':')
                && IsParameterNameStart(sql, index + 1)
                && !(current == ':' && index > 0 && sql[index - 1] == ':'))
            {
                var nameStart = index + 1;
                var nameEnd = nameStart;
                while (nameEnd < sql.Length && IsParameterNamePart(sql[nameEnd]))
                    nameEnd++;

                var parameterName = sql.Substring(nameStart, nameEnd - nameStart);

                // @name = value (z. B. EXEC Proc @id = 42) ist ein benanntes Prozedurargument,
                // kein SQL-Parameter, der hier ersetzt werden muss.
                if (IsFollowedByAssignment(sql, nameEnd))
                {
                    builder.Append(current);
                    builder.Append(parameterName);
                    index = nameEnd - 1;
                    continue;
                }

                if (!parameterMap.TryGetValue(parameterName, out var parameterValue))
                    throw new InvalidOperationException($"Missing value for SQL parameter '{current}{parameterName}'.");

                builder.Append(ToLiteral(parameterValue));
                index = nameEnd - 1;
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    public static string ToLiteral(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "NULL";

        if (TryExtractBinaryLiteralValue(value, out var binaryValue))
            return "'" + Convert.ToBase64String(binaryValue) + "'";

        return value switch
        {
            string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
            bool boolean => boolean ? "TRUE" : "FALSE",
            DateOnly dateOnly => "'" + dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
            TimeOnly timeOnly => "'" + timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'",
            TimeSpan timeSpan => "'" + timeSpan.ToString("c", CultureInfo.InvariantCulture) + "'",
            DateTime dateTime => "'" + dateTime.ToString("O", CultureInfo.InvariantCulture) + "'",
            DateTimeOffset dateTimeOffset => "'" + dateTimeOffset.ToString("O", CultureInfo.InvariantCulture) + "'",
            byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
            Enum enumValue => Convert.ToString(Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType()), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) ?? "NULL",
            _ when IsNumeric(value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
            _ => "'" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''", StringComparison.Ordinal) + "'"
        };
    }

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static bool TryExtractBinaryLiteralValue(object value, out byte[] bytes)
    {
        // Plain byte[] values are rendered as X'...' hex literals below;
        // this helper is reserved for wrapper types that expose a byte[] Id.
        var valueType = value.GetType();
        var property = valueType.GetProperty("Id");
        if (property?.PropertyType == typeof(byte[])
            && property.GetIndexParameters().Length == 0
            && property.GetValue(value) is byte[] propertyBytes)
        {
            bytes = propertyBytes;
            return true;
        }

        var field = valueType.GetField("Id");
        if (field?.FieldType == typeof(byte[]) && field.GetValue(value) is byte[] fieldBytes)
        {
            bytes = fieldBytes;
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    private static bool IsParameterNameStart(string sql, int index)
    {
        if (index >= sql.Length)
            return false;
        var value = sql[index];
        return char.IsLetter(value) || value == '_';
    }

    private static bool IsParameterNamePart(char value)
        => char.IsLetterOrDigit(value) || value == '_';

    private static string NormalizeParameterName(string parameterName)
    {
        if (parameterName.StartsWith("@", StringComparison.Ordinal) || parameterName.StartsWith(":", StringComparison.Ordinal))
            return parameterName[1..];
        return parameterName;
    }

    private static bool IsCreateProcedureStatement(string sql)
    {
        var span = sql.AsSpan().TrimStart();
        if (!span.StartsWith("CREATE ".AsSpan(), StringComparison.OrdinalIgnoreCase)
            && !span.StartsWith("CREATE\t".AsSpan(), StringComparison.OrdinalIgnoreCase)
            && !span.StartsWith("CREATE\n".AsSpan(), StringComparison.OrdinalIgnoreCase)
            && !span.StartsWith("CREATE\r".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "CREATE OR REPLACE PROCEDURE" oder "CREATE PROCEDURE"
        for (var i = 0; i < span.Length; i++)
        {
            if (i + 9 <= span.Length
                && span.Slice(i, 9).Equals("PROCEDURE".AsSpan(), StringComparison.OrdinalIgnoreCase)
                && (i + 9 >= span.Length || !char.IsLetterOrDigit(span[i + 9])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFollowedByAssignment(string sql, int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
            index++;
        return index < sql.Length && sql[index] == '=';
    }
}
