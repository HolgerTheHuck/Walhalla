using System.Text;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace WalhallaSql.EfCore;

internal static class WalhallaSqlJsonProjectionHelper
{
    public static string BuildProjectionName(string sourceColumnName, IReadOnlyList<string> pathSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceColumnName);
        ArgumentNullException.ThrowIfNull(pathSegments);

        var parts = new List<string>(pathSegments.Count + 2) { "json", sourceColumnName };
        parts.AddRange(pathSegments);
        return string.Join("__", parts.Select(SanitizeIdentifierPart));
    }

    public static bool TryBuildProjectionName(ColumnExpression jsonColumn, IReadOnlyList<PathSegment> path, out string projectionName)
    {
        ArgumentNullException.ThrowIfNull(jsonColumn);
        ArgumentNullException.ThrowIfNull(path);

        var propertySegments = new List<string>(path.Count);
        foreach (var segment in path)
        {
            if (segment.PropertyName is not { Length: > 0 } propertyName)
            {
                projectionName = string.Empty;
                return false;
            }

            propertySegments.Add(propertyName);
        }

        projectionName = BuildProjectionName(jsonColumn.Name, propertySegments);
        return true;
    }

    private static string SanitizeIdentifierPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');

        return builder.Length == 0 ? "_" : builder.ToString();
    }
}
