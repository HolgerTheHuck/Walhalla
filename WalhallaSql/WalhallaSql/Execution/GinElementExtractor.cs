using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace WalhallaSql.Execution;

/// <summary>
/// Extracts indexable tokens from JSONB documents for GIN (Generalized Inverted Index) entries.
/// Each token is a UTF-8 encoded string representing a key path or key-value pair
/// contained in the document. These tokens serve as the sort key in the index entry
/// key format: [Sentinel|IndexId|Token|TableId|RowId].
/// </summary>
internal static class GinElementExtractor
{
    /// <summary>
    /// Extracts all indexable elements from a JSONB value.
    /// For a document like <c>{"a": 1, "b": {"c": "hello"}}</c>, extracts:
    /// <c>"a"</c>, <c>"a=1"</c>, <c>"b"</c>, <c>"b.c"</c>, <c>"b.c=hello"</c>.
    /// </summary>
    internal static byte[][] ExtractElements(object? jsonbValue)
    {
        if (jsonbValue == null || jsonbValue == DBNull.Value)
            return Array.Empty<byte[]>();

        var jsonStr = Convert.ToString(jsonbValue);
        if (string.IsNullOrEmpty(jsonStr))
            return Array.Empty<byte[]>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Array.Empty<byte[]>();
        }

        var elements = new List<byte[]>();
        ExtractFromElement(root, "", elements);
        return elements.ToArray();
    }

    private static void ExtractFromElement(JsonElement element, string prefix, List<byte[]> elements)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var keyPath = prefix.Length > 0 ? $"{prefix}.{prop.Name}" : prop.Name;

                    // Add key token: {"keyPath"}
                    elements.Add(Encoding.UTF8.GetBytes(keyPath));

                    // Recurse into nested objects/arrays
                    if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        ExtractFromElement(prop.Value, keyPath, elements);
                    }
                    else
                    {
                        // Scalar value: add key=value token
                        var valueToken = FormatScalarToken(keyPath, prop.Value);
                        elements.Add(Encoding.UTF8.GetBytes(valueToken));
                    }
                }
                break;

            case JsonValueKind.Array:
                for (int i = 0; i < element.GetArrayLength(); i++)
                {
                    var item = element[i];
                    var arrayPath = $"{prefix}[{i}]";

                    if (item.ValueKind == JsonValueKind.Object || item.ValueKind == JsonValueKind.Array)
                    {
                        ExtractFromElement(item, arrayPath, elements);
                    }
                    else
                    {
                        // For scalar arrays, index the value directly with array prefix
                        var valueToken = FormatScalarToken(arrayPath, item);
                        elements.Add(Encoding.UTF8.GetBytes(valueToken));
                    }
                }
                break;

            // Scalars at root level — shouldn't normally happen for GIN-indexed columns
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                if (prefix.Length > 0)
                {
                    var token = FormatScalarToken(prefix, element);
                    elements.Add(Encoding.UTF8.GetBytes(token));
                }
                break;
        }
    }

    /// <summary>
    /// Formats a scalar value token as <c>"keyPath=value"</c>.
    /// </summary>
    private static string FormatScalarToken(string keyPath, JsonElement value)
    {
        var valStr = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => value.GetRawText()
        };
        return $"{keyPath}={valStr}";
    }
}
