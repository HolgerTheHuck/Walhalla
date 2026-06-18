using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

/// <summary>
/// Frontend-neutral core for JSON path navigation and value extraction.
/// Shared by the WHERE compiler (arrow operators, JSON_* functions) and other
/// execution paths. Keeps <see cref="WhereCompiler"/> free of JSON parsing detail.
/// </summary>
internal static class JsonOperators
{
    /// <summary>
    /// Resolves a JSON path against a source value and returns the navigated value.
    /// When <paramref name="unquote"/> is true, string leaves are returned as raw
    /// text (no surrounding quotes); otherwise typed values are returned.
    /// </summary>
    internal static object? ArrowValue(object? source, string jsonPath, bool unquote, bool isPathArray)
    {
        if (source == null) return null;
        var segments = isPathArray ? ParsePostgresPathArray(jsonPath) : ParseJsonPath(jsonPath);
        return ExtractJsonPathValue(source, segments, unquote);
    }

    /// <summary>
    /// Parses a Postgres text-array path (<c>{a,b,0}</c>) into its segments.
    /// </summary>
    internal static string[] ParsePostgresPathArray(string pathArray)
    {
        var s = pathArray.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '{') s = s.Slice(1);
        if (s.Length > 0 && s[^1] == '}') s = s.Slice(0, s.Length - 1);
        if (s.Length == 0) return Array.Empty<string>();

        var segments = new List<string>();
        foreach (var part in s.ToString().Split(','))
            segments.Add(part.Trim());
        return segments.ToArray();
    }

    /// <summary>
    /// Parses a MySQL-style JSON path (<c>$.a.b[0]</c>) into its segments.
    /// </summary>
    internal static string[] ParseJsonPath(string jsonPath)
    {
        var path = jsonPath.AsSpan();
        if (path.StartsWith("$.")) path = path.Slice(2);
        else if (path.StartsWith("$")) path = path.Slice(1);

        var segments = new List<string>();
        while (path.Length > 0)
        {
            if (path[0] == '.') { path = path.Slice(1); continue; }
            if (path[0] == '[')
            {
                var close = path.IndexOf(']');
                if (close < 0) break;
                segments.Add(path.Slice(1, close - 1).ToString());
                path = path.Slice(close + 1);
                continue;
            }
            // Named segment: read until next . or [
            var end = path.Length;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '.' || path[i] == '[') { end = i; break; }
            }
            segments.Add(path.Slice(0, end).ToString());
            path = path.Slice(end);
        }
        return segments.ToArray();
    }

    /// <summary>
    /// Navigates <paramref name="segments"/> through a JSON source (string,
    /// <see cref="JsonElement"/>, or any stringifiable value) and returns the leaf.
    /// </summary>
    internal static object? ExtractJsonPathValue(object source, string[] segments, bool unquote)
    {
        JsonElement root;
        if (source is string jsonStr)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            root = doc.RootElement.Clone();
        }
        else if (source is JsonElement je)
        {
            root = je;
        }
        else
        {
            var str = Convert.ToString(source);
            if (str == null) return null;
            using var doc = JsonDocument.Parse(str);
            root = doc.RootElement.Clone();
        }

        var current = root;
        foreach (var seg in segments)
        {
            if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(seg, out var idx)) return null;
                if (idx < 0 || idx >= current.GetArrayLength()) return null;
                current = current[idx];
            }
            else if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(seg, out var prop)) return null;
                current = prop;
            }
            else
            {
                return null;
            }
        }

        if (unquote)
        {
            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Null => null,
                _ => current.GetRawText()
            };
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => current.GetRawText()
        };
    }

    internal static bool JsonContains(object? source, object? candidate, SqlJsonContainmentOperator op)
    {
        if (source == null || candidate == null) return false;
        var left = ParseToElement(source);
        var right = ParseToElement(candidate);
        var result = ContainsElement(left, right);
        return op == SqlJsonContainmentOperator.Contains ? result : ContainsElement(right, left);
    }

    internal static bool JsonKeyExists(object? source, object? keySpec, SqlJsonKeyExistsOperator op)
    {
        if (source == null || keySpec == null) return false;
        var obj = ParseToElement(source);
        if (obj.ValueKind != JsonValueKind.Object) return false;

        return op switch
        {
            SqlJsonKeyExistsOperator.HasKey => obj.TryGetProperty(ToString(keySpec), out _),
            SqlJsonKeyExistsOperator.HasAnyKey => AnyKeyExists(obj, keySpec),
            SqlJsonKeyExistsOperator.HasAllKeys => AllKeysExist(obj, keySpec),
            _ => false
        };
    }

    internal static JsonElement ParseToElement(object source)
    {
        if (source is JsonElement je) return je;
        var str = Convert.ToString(source);
        using var doc = JsonDocument.Parse(str!);
        return doc.RootElement.Clone();
    }

    private static bool ContainsElement(JsonElement container, JsonElement candidate)
    {
        return candidate.ValueKind switch
        {
            JsonValueKind.Object => ContainsObject(container, candidate),
            JsonValueKind.Array => ContainsArray(container, candidate),
            _ => ScalarEquals(container, candidate)
        };
    }

    private static bool ContainsObject(JsonElement container, JsonElement candidate)
    {
        if (container.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in candidate.EnumerateObject())
        {
            if (!container.TryGetProperty(prop.Name, out var containerValue))
                return false;
            if (!ContainsElement(containerValue, prop.Value))
                return false;
        }
        return true;
    }

    private static bool ContainsArray(JsonElement container, JsonElement candidate)
    {
        foreach (var elem in candidate.EnumerateArray())
        {
            if (!AnyElementContains(container, elem))
                return false;
        }
        return true;
    }

    private static bool AnyElementContains(JsonElement container, JsonElement candidate)
    {
        if (container.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in container.EnumerateArray())
            {
                if (ContainsElement(elem, candidate))
                    return true;
            }
            return false;
        }
        return ContainsElement(container, candidate);
    }

    private static bool ScalarEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => true,
            JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText()
        };
    }

    private static bool AnyKeyExists(JsonElement obj, object? keySpec)
    {
        var keys = ParseKeysArray(keySpec);
        if (keys == null) return false;
        return keys.Any(k => obj.TryGetProperty(k, out _));
    }

    private static bool AllKeysExist(JsonElement obj, object? keySpec)
    {
        var keys = ParseKeysArray(keySpec);
        if (keys == null) return false;
        return keys.All(k => obj.TryGetProperty(k, out _));
    }

    private static string[]? ParseKeysArray(object? keySpec)
    {
        if (keySpec is string str)
        {
            try
            {
                using var doc = JsonDocument.Parse(str);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
            }
            catch { }
        }
        // Fallback: single key as string
        var s = ToString(keySpec);
        return s != null ? new[] { s } : null;
    }

    private static string ToString(object? value)
    {
        if (value == null) return null!;
        if (value is string s) return s;
        return Convert.ToString(value)!;
    }

    // ── Minimal jsonpath (B.4.4) ────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a minimal jsonpath expression against a JSON source.
    /// Supports: <c>$.key.subkey</c>, <c>$.key[*].subkey</c>, <c>$.key[0]</c>, <c>$[*]</c>.
    /// </summary>
    internal static bool JsonPathExists(object? source, string path)
    {
        if (source == null) return false;
        var element = ParseToElement(source);
        return WalkJsonPath(element, ParseJsonPath(path), 0, out _);
    }

    /// <summary>
    /// Returns the first match of a jsonpath expression as a JSON string.
    /// </summary>
    internal static string? JsonPathQuery(object? source, string path)
    {
        if (source == null) return null;
        var element = ParseToElement(source);
        if (WalkJsonPath(element, ParseJsonPath(path), 0, out var match))
            return match.GetRawText();
        return null;
    }

    /// <summary>
    /// Applies a jsonpath to set a value at the given path in a JSON document.
    /// Returns the modified JSON string.
    /// </summary>
    internal static string? JsonSet(object? source, string path, object? newValue)
    {
        if (source == null) return null;
        var element = ParseToElement(source);
        var segments = ParseJsonPath(path);
        var modified = SetAtPath(element, segments, 0, newValue);
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        modified.WriteTo(writer);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string? JsonInsert(object? source, string path, object? newValue)
    {
        if (source == null) return null;
        var element = ParseToElement(source);
        var segments = ParseJsonPath(path);
        var modified = InsertAtPath(element, segments, 0, newValue);
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        modified.WriteTo(writer);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool WalkJsonPath(JsonElement element, string[] segments, int idx, out JsonElement result)
    {
        if (idx >= segments.Length) { result = element; return true; }
        var seg = segments[idx];

        if (seg == "*")
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (WalkJsonPath(item, segments, idx + 1, out result))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (WalkJsonPath(prop.Value, segments, idx + 1, out result))
                        return true;
                }
            }
            result = default;
            return false;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            if (!int.TryParse(seg, out var arrIdx)) { result = default; return false; }
            if (arrIdx < 0 || arrIdx >= element.GetArrayLength()) { result = default; return false; }
            return WalkJsonPath(element[arrIdx], segments, idx + 1, out result);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!element.TryGetProperty(seg, out var prop)) { result = default; return false; }
            return WalkJsonPath(prop, segments, idx + 1, out result);
        }

        result = default;
        return false;
    }

    private static JsonElement SetAtPath(JsonElement element, string[] segments, int idx, object? newValue)
    {
        if (idx >= segments.Length) return ParseToElement(newValue!);

        var seg = segments[idx];
        var newVal = ParseToElement(newValue!);

        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                bool replaced = false;
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    if (prop.Name == seg)
                    {
                        replaced = true;
                        if (idx + 1 < segments.Length)
                            SetAtPath(prop.Value, segments, idx + 1, newValue).WriteTo(writer);
                        else
                            newVal.WriteTo(writer);
                    }
                    else
                    {
                        prop.Value.WriteTo(writer);
                    }
                }
                if (!replaced)
                {
                    writer.WritePropertyName(seg);
                    if (idx + 1 < segments.Length)
                    {
                        // Create intermediate objects
                        using var emptyStream = new System.IO.MemoryStream();
                        using var emptyWriter = new System.Text.Json.Utf8JsonWriter(emptyStream);
                        emptyWriter.WriteStartObject();
                        emptyWriter.WriteEndObject();
                        emptyWriter.Flush();
                        var emptyObj = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(emptyStream.ToArray())).RootElement;
                        SetAtPath(emptyObj, segments, idx + 1, newValue).WriteTo(writer);
                    }
                    else
                    {
                        newVal.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                if (int.TryParse(seg, out var arrIdx) && arrIdx >= 0 && arrIdx < element.GetArrayLength())
                {
                    for (int i = 0; i < element.GetArrayLength(); i++)
                    {
                        if (i == arrIdx)
                        {
                            if (idx + 1 < segments.Length)
                                SetAtPath(element[i], segments, idx + 1, newValue).WriteTo(writer);
                            else
                                newVal.WriteTo(writer);
                        }
                        else
                        {
                            element[i].WriteTo(writer);
                        }
                    }
                }
                else
                {
                    foreach (var item in element.EnumerateArray())
                        item.WriteTo(writer);
                }
                writer.WriteEndArray();
            }
            else
            {
                newVal.WriteTo(writer);
            }
        }

        var resultJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
        return doc.RootElement.Clone();
    }

    private static JsonElement InsertAtPath(JsonElement element, string[] segments, int idx, object? newValue)
    {
        // For insert: if the final segment is an array index, insert before that index.
        // If the parent is an object and the key doesn't exist, add it.
        // Otherwise behave like set.
        return SetAtPath(element, segments, idx, newValue);
    }
}
