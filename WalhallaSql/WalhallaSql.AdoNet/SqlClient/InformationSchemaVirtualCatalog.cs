using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using WalhallaSql.Sql;

namespace WalhallaSql.AdoNet.SqlClient;

/// <summary>
/// Virtual information_schema catalog that intercepts queries for
/// <c>information_schema.columns</c> and <c>information_schema.tables</c>
/// before they reach the WalhallaSql engine.
/// </summary>
internal static class InformationSchemaVirtualCatalog
{
    private const string TableSchemaFilterColumn = "table_schema";
    private const string TableNameFilterColumn = "table_name";
    private const string DefaultSchema = "public";

    public static bool TryResolveVirtualQuery(
        string sql,
        IReadOnlyList<SqlTableDefinition> tables,
        out SqlExecutionResult result)
    {
        result = default!;

        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var normalized = NormalizeForCatalogDetection(sql);

        if (normalized.Contains("information_schema.columns"))
            return TryResolveColumnsQuery(normalized, tables, out result);

        if (normalized.Contains("information_schema.tables"))
            return TryResolveTablesQuery(normalized, tables, out result);

        return false;
    }

    private static bool TryResolveColumnsQuery(
        string normalizedSql,
        IReadOnlyList<SqlTableDefinition> tables,
        out SqlExecutionResult result)
    {
        var selectedColumns = ExtractSelectedColumns(normalizedSql);
        var whereFilters = ExtractWhereFilters(normalizedSql);
        var orderByColumns = ExtractOrderByColumns(normalizedSql);

        // Build rows: one row per column of every table
        var rows = new List<Dictionary<string, object?>>();
        foreach (var table in tables.OrderBy(static t => t.CollectionName, StringComparer.OrdinalIgnoreCase))
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var col = table.Columns[i];
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = "App",
                    ["table_schema"] = DefaultSchema,
                    ["table_name"] = table.CollectionName,
                    ["column_name"] = col.Name,
                    ["ordinal_position"] = i + 1,
                    ["is_nullable"] = col.IsNullable ? "YES" : "NO",
                    ["data_type"] = MapSqlType(col.Type),
                    ["character_maximum_length"] = col.Type == SqlScalarType.String ? 2147483647 : (object?)null,
                    ["numeric_precision"] = col.Type is SqlScalarType.Int32 or SqlScalarType.Int64 or SqlScalarType.Double ? 53 : (object?)null,
                    ["is_primary_key"] = col.IsPrimaryKey ? "YES" : "NO",
                };
                rows.Add(row);
            }
        }

        // Apply WHERE filters
        if (whereFilters.Count > 0)
        {
            rows = rows.Where(row =>
                    whereFilters.All(filter =>
                        row.TryGetValue(filter.Key, out var rowValue)
                        && string.Equals(rowValue?.ToString(), filter.Value, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // Apply ORDER BY
        if (orderByColumns.Count > 0)
        {
            rows = ApplyOrderBy(rows, orderByColumns);
        }

        // Apply column projection (SELECT list)
        var columns = ComputeOutputColumns(selectedColumns, ColumnsColumns);

        var projectedRows = ProjectRows(rows, columns);

        result = new SqlExecutionResult(projectedRows.Count, projectedRows);
        return true;
    }

    private static bool TryResolveTablesQuery(
        string normalizedSql,
        IReadOnlyList<SqlTableDefinition> tables,
        out SqlExecutionResult result)
    {
        var selectedColumns = ExtractSelectedColumns(normalizedSql);
        var whereFilters = ExtractWhereFilters(normalizedSql);

        var rows = tables
            .OrderBy(static t => t.CollectionName, StringComparer.OrdinalIgnoreCase)
            .Select(table => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["table_catalog"] = "App",
                ["table_schema"] = DefaultSchema,
                ["table_name"] = table.CollectionName,
                ["table_type"] = "BASE TABLE",
            })
            .ToList();

        if (whereFilters.Count > 0)
        {
            rows = rows.Where(row =>
                    whereFilters.All(filter =>
                        row.TryGetValue(filter.Key, out var rowValue)
                        && string.Equals(rowValue?.ToString(), filter.Value, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var columns = ComputeOutputColumns(selectedColumns, TablesColumns);
        var projectedRows = ProjectRows(rows, columns);

        result = new SqlExecutionResult(projectedRows.Count, projectedRows);
        return true;
    }

    // Maps SqlScalarType to PostgreSQL-compatible information_schema type names
    private static string MapSqlType(SqlScalarType type)
    {
        return type switch
        {
            SqlScalarType.Int32 => "integer",
            SqlScalarType.Int64 => "bigint",
            SqlScalarType.Double => "double precision",
            SqlScalarType.Decimal => "numeric",
            SqlScalarType.Boolean => "boolean",
            SqlScalarType.DateTime => "timestamp without time zone",
            SqlScalarType.String => "text",
            SqlScalarType.Binary => "bytea",
            SqlScalarType.Guid => "uuid",
            SqlScalarType.Json => "text",
            SqlScalarType.Unknown => "text",
            _ => "text",
        };
    }

    private static readonly IReadOnlyList<(string Name, Type ClrType)> ColumnsColumns = new[]
    {
        ("table_catalog", typeof(string)),
        ("table_schema", typeof(string)),
        ("table_name", typeof(string)),
        ("column_name", typeof(string)),
        ("ordinal_position", typeof(int)),
        ("is_nullable", typeof(string)),
        ("data_type", typeof(string)),
        ("character_maximum_length", typeof(int)),
        ("numeric_precision", typeof(int)),
        ("is_primary_key", typeof(string)),
    };

    private static readonly IReadOnlyList<(string Name, Type ClrType)> TablesColumns = new[]
    {
        ("table_catalog", typeof(string)),
        ("table_schema", typeof(string)),
        ("table_name", typeof(string)),
        ("table_type", typeof(string)),
    };

    private static IReadOnlyList<string> ExtractSelectedColumns(string normalizedSql)
    {
        var fromIdx = FindKeyword(normalizedSql, "from", 0);
        if (fromIdx < 0)
            return Array.Empty<string>();

        var projection = normalizedSql["select".Length..fromIdx].Trim();
        if (projection.Trim() == "*")
            return Array.Empty<string>();

        var columns = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;

        for (var i = 0; i < projection.Length; i++)
        {
            var c = projection[i];

            if (c == '\'' && !inString)
            {
                inString = true;
                continue;
            }

            if (c == '\'' && inString)
            {
                if (i + 1 < projection.Length && projection[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inString = false;
                continue;
            }

            if (inString) continue;

            if (c == '(') depth++;
            if (c == ')') depth--;

            if (depth == 0 && c == ',')
            {
                columns.Add(NormalizeProjectionColumnName(projection[start..i]));
                start = i + 1;
            }
        }

        // Last column
        if (start < projection.Length)
            columns.Add(NormalizeProjectionColumnName(projection[start..]));

        return columns;
    }

    private static string NormalizeProjectionColumnName(string projection)
    {
        var trimmed = projection.Trim();

        // Handle "expr AS alias"
        var aliasMatch = Regex.Match(trimmed, @"\s+as\s+(?<alias>[\w""\[\]`]+)$", RegexOptions.IgnoreCase);
        if (aliasMatch.Success)
            return UnquoteIdentifier(aliasMatch.Groups["alias"].Value.ToLowerInvariant());

        // Handle qualified columns like "table.column" or just "column"
        var dotIdx = trimmed.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx + 1 < trimmed.Length)
            return UnquoteIdentifier(trimmed[(dotIdx + 1)..]).ToLowerInvariant();

        return UnquoteIdentifier(trimmed).ToLowerInvariant();
    }

    private static string UnquoteIdentifier(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2)
        {
            if ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '`' && trimmed[^1] == '`')
                || (trimmed[0] == '[' && trimmed[^1] == ']'))
                return trimmed[1..^1];
        }
        return trimmed;
    }

    private static Dictionary<string, string> ExtractWhereFilters(string normalizedSql)
    {
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var whereIdx = FindKeyword(normalizedSql, "where", 0);
        if (whereIdx < 0)
            return filters;

        var whereClause = ExtractWhereClause(normalizedSql, whereIdx);
        if (string.IsNullOrWhiteSpace(whereClause))
            return filters;

        // Split on AND/and
        var conditions = SplitTopLevel(whereClause, " and ");
        foreach (var condition in conditions)
        {
            var trimmed = condition.Trim().Trim('(', ')', ' ').Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Parse "column_name = 'value'" or "column_name = 'value'"
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0) continue;

            var column = trimmed[..eqIdx].Trim().Trim('"').Trim().ToLowerInvariant();
            var value = trimmed[(eqIdx + 1)..].Trim();
            if (value.StartsWith("'") && value.EndsWith("'"))
                value = value[1..^1];

            filters[column] = value;
        }

        return filters;
    }

    private static string ExtractWhereClause(string sql, int whereIdx)
    {
        // Extract from WHERE keyword to end of string, ORDER BY, GROUP BY, or LIMIT
        var body = sql[(whereIdx + 5)..];

        var endIdx = int.MaxValue;
        foreach (var kw in new[] { "order by", "group by", "having", "limit", "offset", "fetch" })
        {
            var idx = FindKeyword(body, kw, 0);
            if (idx >= 0 && idx < endIdx)
                endIdx = idx;
        }

        if (endIdx < body.Length)
            body = body[..endIdx];

        return body.Trim();
    }

    private static List<Dictionary<string, object?>> ApplyOrderBy(
        List<Dictionary<string, object?>> rows,
        IReadOnlyList<(string Column, bool Descending)> orderBy)
    {
        var sorted = new List<Dictionary<string, object?>>(rows);
        sorted.Sort((a, b) =>
        {
            foreach (var (col, desc) in orderBy)
            {
                var aVal = a.TryGetValue(col, out var av) ? av : null;
                var bVal = b.TryGetValue(col, out var bv) ? bv : null;
                var cmp = CompareValues(aVal, bVal);
                if (cmp != 0) return desc ? -cmp : cmp;
            }
            return 0;
        });
        return sorted;
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        if (a is int ai && b is int bi) return ai.CompareTo(bi);
        if (a is long al && b is long bl) return al.CompareTo(bl);
        if (a is string sa && b is string sb) return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);

        return string.Compare(
            Convert.ToString(a, CultureInfo.InvariantCulture),
            Convert.ToString(b, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<(string Column, bool Descending)> ExtractOrderByColumns(string normalizedSql)
    {
        var result = new List<(string Column, bool Descending)>();
        var orderByIdx = FindKeyword(normalizedSql, "order by", 0);
        if (orderByIdx < 0)
            return result;

        var orderByClause = normalizedSql[(orderByIdx + 8)..];
        // Stop at LIMIT, OFFSET, FETCH, or end
        foreach (var kw in new[] { "limit", "offset", "fetch" })
        {
            var idx = FindKeyword(orderByClause, kw, 0);
            if (idx >= 0)
            {
                orderByClause = orderByClause[..idx];
                break;
            }
        }

        var parts = SplitTopLevel(orderByClause.Trim(), ",");
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var descending = false;
            if (trimmed.EndsWith(" desc", StringComparison.OrdinalIgnoreCase))
            {
                descending = true;
                trimmed = trimmed[..^5].Trim();
            }
            else if (trimmed.EndsWith(" asc", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^4].Trim();
            }

            var column = NormalizeProjectionColumnName(trimmed);
            if (!string.IsNullOrWhiteSpace(column))
                result.Add((column, descending));
        }

        return result;
    }

    private static IReadOnlyList<(string Name, Type ClrType)> ComputeOutputColumns(
        IReadOnlyList<string> selectedColumns,
        IReadOnlyList<(string Name, Type ClrType)> fallbackColumns)
    {
        if (selectedColumns.Count == 0)
            return fallbackColumns.ToList();

        var result = new List<(string Name, Type ClrType)>();
        foreach (var col in selectedColumns)
        {
            var match = fallbackColumns.FirstOrDefault(fc =>
                string.Equals(fc.Name, col, StringComparison.OrdinalIgnoreCase));
            result.Add(match == default ? (col, typeof(string)) : match);
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProjectRows(
        List<Dictionary<string, object?>> rows,
        IReadOnlyList<(string Name, Type ClrType)> columns)
    {
        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var projected = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, _) in columns)
                projected[name] = row.TryGetValue(name, out var value) ? value : null;
            result.Add(projected);
        }
        return result;
    }

    private static IReadOnlyList<string> SplitTopLevel(string text, string separator)
    {
        var segments = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\'' && !inString)
            {
                inString = true;
                i++;
                continue;
            }

            if (c == '\'' && inString)
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }
                inString = false;
                i++;
                continue;
            }

            if (inString)
            {
                i++;
                continue;
            }

            if (c == '(') depth++;
            if (c == ')') depth--;

            if (depth == 0 && IsMatchAt(text, i, separator))
            {
                segments.Add(text[start..i].Trim());
                start = i + separator.Length;
                i = start;
                continue;
            }

            i++;
        }

        if (start <= text.Length)
            segments.Add(text[start..].Trim());

        return segments;
    }

    private static bool IsMatchAt(string text, int index, string separator)
    {
        if (index + separator.Length > text.Length)
            return false;

        return string.Compare(text, index, separator, 0, separator.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static int FindKeyword(string text, string keyword, int startIndex)
    {
        for (var i = Math.Max(0, startIndex); i <= text.Length - keyword.Length; i++)
        {
            if (string.Compare(text, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;

            // Check word boundary before
            if (i > 0 && char.IsLetterOrDigit(text[i - 1]))
                continue;

            // Check word boundary after
            var afterIdx = i + keyword.Length;
            if (afterIdx < text.Length && char.IsLetterOrDigit(text[afterIdx]))
                continue;

            return i;
        }

        return -1;
    }

    private static string NormalizeForCatalogDetection(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return string.Empty;

        var normalized = Regex.Replace(sql, @"\s+", " ").Trim().ToLowerInvariant();
        normalized = normalized.Replace("\"", string.Empty, StringComparison.Ordinal);
        return normalized;
    }
}
