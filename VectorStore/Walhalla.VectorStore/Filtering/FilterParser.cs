// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Walhalla.Indexes.FullText;

namespace Walhalla.VectorStore.Filtering;

/// <summary>
/// Parst eine Qdrant-aehnliche JSON-Filter-DSL in einen <see cref="FilterClause"/u003e AST.
/// </summary>
public static class FilterParser
{
    /// <summary>
    /// Parst ein <code>JsonElement</code>, das ein Objekt mit <code>must</code>,
    /// <code>should</code> und/oder <code>must_not</code> enthaelt.
    /// </summary>
    public static FilterClause Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new FilterParseException("Filter muss ein JSON-Objekt sein.");

        Condition[]? must = null;
        Condition[]? should = null;
        Condition[]? mustNot = null;

        foreach (var property in json.EnumerateObject())
        {
            switch (property.Name)
            {
                case "must":
                    must = ParseConditions(property.Value);
                    break;
                case "should":
                    should = ParseConditions(property.Value);
                    break;
                case "must_not":
                    mustNot = ParseConditions(property.Value);
                    break;
                default:
                    throw new FilterParseException($"Unbekannte Filter-Eigenschaft: '{property.Name}'. Erwartet: must, should, must_not.");
            }
        }

        if (must is null && should is null && mustNot is null)
            throw new FilterParseException("Filter muss mindestens eine Bedingung enthalten (must, should oder must_not).");

        return new FilterClause(must ?? Array.Empty<Condition>(),
                                should,
                                mustNot);
    }

    /// <summary>Parst ein Filter-JSON-String.</summary>
    public static FilterClause Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    /// <summary>Parst ein Dictionary (aus Minimal-API-Binding).</summary>
    public static FilterClause Parse(Dictionary<string, JsonElement> dict)
    {
        // Serialisiere das Dictionary kurz zu einem JsonElement
        var json = JsonSerializer.Serialize(dict);
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    private static Condition[] ParseConditions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new FilterParseException("must/should/must_not muss ein JSON-Array sein.");

        var conditions = new List<Condition>();
        foreach (var item in element.EnumerateArray())
        {
            conditions.Add(ParseCondition(item));
        }
        return conditions.ToArray();
    }

    private static Condition ParseCondition(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FilterParseException("Jede Bedingung muss ein JSON-Objekt sein.");

        string? key = null;
        JsonElement? match = null;
        JsonElement? range = null;
        JsonElement? geoRadius = null;
        JsonElement? fullText = null;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "key":
                    key = property.Value.GetString();
                    break;
                case "match":
                    match = property.Value;
                    break;
                case "range":
                    range = property.Value;
                    break;
                case "geo_radius":
                    geoRadius = property.Value;
                    break;
                case "full_text":
                    fullText = property.Value;
                    break;
                default:
                    throw new FilterParseException($"Unbekannte Bedingung-Eigenschaft: '{property.Name}'. Erwartet: key, match, range, geo_radius, full_text.");
            }
        }

        if (string.IsNullOrEmpty(key))
            throw new FilterParseException("Jede Bedingung benoetigt ein 'key'-Feld.");

        var conditionCount = (match.HasValue ? 1 : 0) + (range.HasValue ? 1 : 0) + (geoRadius.HasValue ? 1 : 0) + (fullText.HasValue ? 1 : 0);
        if (conditionCount != 1)
            throw new FilterParseException("Eine Bedingung darf genau einen von 'match', 'range', 'geo_radius' oder 'full_text' enthalten.");

        if (match.HasValue)
        {
            var matchValue = ParseMatchValue(match.Value);
            return new MatchCondition(key, matchValue);
        }

        if (range.HasValue)
        {
            var rangeValue = ParseRangeValue(range.Value);
            return new RangeCondition(key, rangeValue);
        }

        if (geoRadius.HasValue)
        {
            return ParseGeoRadiusValue(key, geoRadius.Value);
        }

        return ParseFullTextValue(key, fullText!.Value);
    }

    private static MatchValue ParseMatchValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FilterParseException("match muss ein JSON-Objekt sein.");

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "value":
                    return ParseScalarMatchValue(property.Value);
                default:
                    throw new FilterParseException($"Unbekannte match-Eigenschaft: '{property.Name}'. Erwartet: value.");
            }
        }

        throw new FilterParseException("match muss ein 'value'-Feld enthalten.");
    }

    private static MatchValue ParseScalarMatchValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => new MatchString(element.GetString()!),
            JsonValueKind.Number when element.TryGetInt64(out var l) => new MatchInt(l),
            JsonValueKind.Number => new MatchDouble(element.GetDouble()),
            JsonValueKind.True => new MatchBool(true),
            JsonValueKind.False => new MatchBool(false),
            _ => throw new FilterParseException($"Unbekannter Wert-Typ in match.value: {element.ValueKind}")
        };
    }

    private static GeoRadiusCondition ParseGeoRadiusValue(string key, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FilterParseException("geo_radius muss ein JSON-Objekt sein.");

        double? lat = null;
        double? lon = null;
        double? radius = null;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "lat":
                    lat = property.Value.GetDouble();
                    break;
                case "lon":
                    lon = property.Value.GetDouble();
                    break;
                case "radius":
                    radius = property.Value.GetDouble();
                    break;
                default:
                    throw new FilterParseException($"Unbekannte geo_radius-Eigenschaft: '{property.Name}'. Erwartet: lat, lon, radius.");
            }
        }

        if (!lat.HasValue || !lon.HasValue || !radius.HasValue)
            throw new FilterParseException("geo_radius benoetigt lat, lon und radius.");

        return new GeoRadiusCondition(key, lat.Value, lon.Value, radius.Value);
    }

    private static FullTextCondition ParseFullTextValue(string key, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FilterParseException("full_text muss ein JSON-Objekt sein.");

        string? query = null;
        string? notQuery = null;
        var mode = FullTextQueryMode.All;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "query":
                    query = property.Value.GetString();
                    break;
                case "mode":
                    mode = ParseFullTextMode(property.Value.GetString());
                    break;
                case "not":
                    notQuery = property.Value.GetString();
                    break;
                default:
                    throw new FilterParseException($"Unbekannte full_text-Eigenschaft: '{property.Name}'. Erwartet: query, mode, not.");
            }
        }

        if (string.IsNullOrEmpty(query))
            throw new FilterParseException("full_text benoetigt ein 'query'-Feld.");

        return new FullTextCondition(key, query, mode, notQuery);
    }

    private static FullTextQueryMode ParseFullTextMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            return FullTextQueryMode.All;

        if (string.Equals(mode, "any", StringComparison.OrdinalIgnoreCase))
            return FullTextQueryMode.Any;

        throw new FilterParseException($"Unbekannter full_text-Modus '{mode}'. Erwartet: all oder any.");
    }

    private static RangeValue ParseRangeValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FilterParseException("range muss ein JSON-Objekt sein.");

        long? gt = null;
        long? gte = null;
        long? lt = null;
        long? lte = null;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "gt":
                    gt = property.Value.GetInt64();
                    break;
                case "gte":
                    gte = property.Value.GetInt64();
                    break;
                case "lt":
                    lt = property.Value.GetInt64();
                    break;
                case "lte":
                    lte = property.Value.GetInt64();
                    break;
                default:
                    throw new FilterParseException($"Unbekannte range-Eigenschaft: '{property.Name}'. Erwartet: gt, gte, lt, lte.");
            }
        }

        return new RangeValue(gt, gte, lt, lte);
    }
}
