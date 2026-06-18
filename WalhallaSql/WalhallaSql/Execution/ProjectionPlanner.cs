using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

internal static class ProjectionPlanner
{
    // Per-table-definition column index cache. Keyed by reference identity so that
    // ALTER TABLE (which creates a new definition via 'with') automatically gets a
    // fresh cache without explicit invalidation.
    private static readonly ConditionalWeakTable<SqlTableDefinition, Dictionary<string, int>> _columnIndexCache = new();

    public static ProjectionPlan Build(IReadOnlyList<SqlSelectColumn> columns, SqlTableDefinition table)
    {
        if (columns.Count == 1 && columns[0].Expression == "*")
        {
            var allIndices = new int[table.Columns.Count];
            var allNames = new string[table.Columns.Count];
            for (int i = 0; i < table.Columns.Count; i++)
            {
                allIndices[i] = i;
                allNames[i] = table.Columns[i].Name;
            }
            return new ProjectionPlan(allIndices, allNames);
        }

        var indices = new List<int>();
        var names = new List<string>();
        var evaluators = new List<Func<object?[], object?>?>();

        foreach (var col in columns)
        {
            // Aggregate or window function columns don't exist in the table — use expression as name.
            if (col.Aggregate != null || col.WindowFunction != null)
            {
                names.Add(col.Alias ?? col.Expression);
                indices.Add(0); // dummy, unused for aggregate/window queries
                evaluators.Add(null);
                continue;
            }

            // Normalisiere den Spalten-Bezeichner (entferne SQL-Quotes wie "c"."Id" → c.Id).
            var sourceName = SqlSyntaxText.NormalizeIdentifier(col.Expression.Trim());
            // Für JOIN-Queries die qualifizierten Spaltennamen (z.B. "c.Id") als
            // Output-Namen verwenden, damit Spalten aus verschiedenen Tabellen
            // unterscheidbar bleiben (kein Überschreiben von "Id" durch "t0.Id").
            var outputName = col.Alias
                ?? (sourceName.Contains('.') && FindColumnIndex(table, sourceName) >= 0
                    ? sourceName
                    : GetUnqualifiedName(sourceName));

            var idx = FindColumnIndex(table, sourceName);
            if (idx >= 0)
            {
                indices.Add(idx);
                names.Add(outputName);
                evaluators.Add(null);
            }
            else
            {
                // Check JSON projections (virtual columns like json__Profile__Name).
                // Use the unqualified source name (without table qualifier) because
                // the alias may differ from the projection name.
                // E.g. source="j.json__Profile__Name" with alias="Name" → look up "json__Profile__Name".
                var unqualifiedSource = GetUnqualifiedName(sourceName);
                Func<object?[], object?>? evaluator = TryBuildProjectionEvaluator(table, unqualifiedSource);

                // If not a projection, try to parse as a value expression (COALESCE, CASE, scalar function, etc.)
                if (evaluator == null)
                {
                    try
                    {
                        var valueExpr = SqlWhereParser.ParseValueExpression(sourceName);
                        evaluator = WhereCompiler.CompileValue(valueExpr, table);
                    }
                    catch
                    {
                        // If parsing fails, fall through to error below.
                    }
                }

                if (evaluator != null)
                {
                    indices.Add(-1); // sentinel: computed column
                    names.Add(outputName);
                    evaluators.Add(evaluator);
                }
                else
                {
                    throw new WalhallaException($"Column '{sourceName}' not found in table '{table.CollectionName}'.");
                }
            }
        }

        var evaluatorArray = evaluators.Any(e => e != null) ? evaluators.ToArray() : null;
        return new ProjectionPlan(indices.ToArray(), names.ToArray(), evaluatorArray);
    }

    /// <summary>
    /// Versucht, einen Evaluator für eine JSON-Projektionsspalte zu bauen.
    /// </summary>
    private static Func<object?[], object?>? TryBuildProjectionEvaluator(SqlTableDefinition table, string sourceName)
    {
        if (table.Projections == null) return null;

        foreach (var proj in table.Projections)
        {
            if (!string.Equals(proj.ProjectionName, sourceName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Finde die Quell-Spalte (die JSON-Spalte)
            var sourceColIdx = FindColumnIndex(table, proj.SourceColumnName);
            if (sourceColIdx < 0) return null;

            var pathSegments = proj.PathSegments;
            var resultType = proj.ResultType;

            return row =>
            {
                var jsonValue = row[sourceColIdx];
                if (jsonValue == null || jsonValue == DBNull.Value) return null;

                var jsonString = jsonValue.ToString()!;
                try
                {
                    using var doc = JsonDocument.Parse(jsonString);
                    var element = doc.RootElement;

                    // Traversiere den JSON-Pfad
                    foreach (var segment in pathSegments)
                    {
                        if (element.ValueKind == JsonValueKind.Object)
                        {
                            if (!element.TryGetProperty(segment, out element))
                                return null;
                        }
                        else
                        {
                            return null;
                        }
                    }

                    // Konvertiere zum Zieltyp
                    return ConvertJsonElement(element, resultType);
                }
                catch
                {
                    return null;
                }
            };
        }

        return null;
    }

    private static object? ConvertJsonElement(JsonElement element, SqlScalarType targetType)
    {
        return targetType switch
        {
            SqlScalarType.String => element.GetString(),
            SqlScalarType.Int32 => element.ValueKind == JsonValueKind.Number ? element.GetInt32() : null,
            SqlScalarType.Int64 => element.ValueKind == JsonValueKind.Number ? element.GetInt64() : null,
            SqlScalarType.Double => element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null,
            SqlScalarType.Decimal => element.ValueKind == JsonValueKind.Number ? element.GetDecimal() : null,
            SqlScalarType.Boolean => element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False ? element.GetBoolean() : null,
            SqlScalarType.DateTime => element.ValueKind == JsonValueKind.String ? DateTime.Parse(element.GetString()!) : null,
            SqlScalarType.Guid => element.ValueKind == JsonValueKind.String ? Guid.Parse(element.GetString()!) : null,
            _ => element.GetString() ?? element.GetRawText()
        };
    }

    private static string GetUnqualifiedName(string expression)
    {
        var dot = expression.LastIndexOf('.');
        if (dot < 0) return expression;
        // Only strip simple qualifiers; leave expressions with function calls or operators alone.
        var before = expression[..dot].Trim();
        var after = expression[(dot + 1)..].Trim();
        if (before.IndexOfAny(new[] { ' ', '(', ')', '\'', '"', '[', '`' }) >= 0)
            return expression;
        if (after.IndexOfAny(new[] { ' ', '(', ')', '\'', '"', '[', '`' }) >= 0)
            return expression;
        return after;
    }

    private static int FindColumnIndex(SqlTableDefinition table, string name)
    {
        var cache = _columnIndexCache.GetValue(table, static t =>
        {
            var c = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < t.Columns.Count; i++)
                c[t.Columns[i].Name] = i;
            return c;
        });

        return cache.TryGetValue(name, out var idx) ? idx : -1;
    }
}

internal sealed record ProjectionPlan(int[] Indices, string[] Names, Func<object?[], object?>?[]? ComputedEvaluators = null);
