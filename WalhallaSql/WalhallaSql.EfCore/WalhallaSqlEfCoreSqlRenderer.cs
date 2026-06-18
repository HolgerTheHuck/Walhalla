using System.Globalization;
using System.Text.Json;
using WalhallaSql.AdoNet.SqlClient;
using WalhallaSql.Sql;
using Microsoft.EntityFrameworkCore.Metadata;

namespace WalhallaSql.EfCore;

internal static class WalhallaSqlEfCoreSqlRenderer
{
    public static object? ToProviderValue(object? clrValue, IProperty? property)
    {
        if (property == null)
            return clrValue;

        var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;
        if (converter != null)
            return converter.ConvertToProvider(clrValue);

        if (clrValue != null)
        {
            var clrType = clrValue.GetType();
            if (clrType.IsEnum)
                return Convert.ChangeType(clrValue, Enum.GetUnderlyingType(clrType), CultureInfo.InvariantCulture);
        }

        return clrValue;
    }

    public static string FormatSqlLiteral(object? clrValue, IProperty? property)
    {
        return FormatProviderSqlLiteral(ToProviderValue(clrValue, property), property);
    }

    public static string FormatProviderSqlLiteral(object? providerValue, IProperty? property)
    {
        return SqlLiteralFormatter.ToLiteral(providerValue);
    }

    public static string? RenderEqualityWhereClause(string collectionName, IReadOnlyList<(string ColumnName, object? Value, IProperty? Property)> filters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(filters);

        if (filters.Count == 0)
            return null;

        var clauses = new List<string>(filters.Count);
        foreach (var filter in filters)
        {
            var providerValue = ToProviderValue(filter.Value, filter.Property);
            clauses.Add($"{filter.ColumnName} = {SqlLiteralFormatter.ToLiteral(providerValue)}");
        }

        return string.Join(" AND ", clauses);
    }

}
