// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Walhalla.Indexes.FullText;

namespace Walhalla.VectorStore.Filtering;

/// <summary>
/// Wertet einen <see cref="FilterClause"/u003e gegen ein Payload-Dictionary aus.
/// </summary>
public static class FilterEvaluator
{
    /// <summary>
    /// Prueft, ob das Payload die Filter-Bedingungen erfuellt.
    /// </summary>
    public static bool Evaluate(FilterClause clause, Dictionary<string, object>? payload)
    {
        if (payload is null)
            return clause.Must.Length == 0 && (clause.Should is null || clause.Should.Length == 0) && (clause.MustNot is null || clause.MustNot.Length == 0);

        // Must: Alle Bedingungen muessen true sein (AND)
        foreach (var condition in clause.Must)
        {
            if (!EvaluateCondition(condition, payload))
                return false;
        }

        // MustNot: Keine Bedingung darf true sein (NOT)
        if (clause.MustNot is not null)
        {
            foreach (var condition in clause.MustNot)
            {
                if (EvaluateCondition(condition, payload))
                    return false;
            }
        }

        // Should: Mindestens eine Bedingung muss true sein (OR) – nur relevant wenn Must leer
        if (clause.Should is not null && clause.Should.Length > 0)
        {
            if (clause.Must.Length == 0)
            {
                var any = false;
                foreach (var condition in clause.Should)
                {
                    if (EvaluateCondition(condition, payload))
                    {
                        any = true;
                        break;
                    }
                }
                if (!any)
                    return false;
            }
            // Wenn Must nicht leer ist, ist Should optional (kein Einfluss auf das Ergebnis)
        }

        return true;
    }

    private static bool EvaluateCondition(Condition condition, Dictionary<string, object> payload)
    {
        return condition switch
        {
            MatchCondition match => EvaluateMatch(match, payload),
            RangeCondition range => EvaluateRange(range, payload),
            GeoRadiusCondition geo => EvaluateGeoRadius(geo, payload),
            FullTextCondition ft => EvaluateFullText(ft, payload),
            _ => false
        };
    }

    private static bool EvaluateMatch(MatchCondition condition, Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue(condition.Key, out var rawValue))
            return false;

        var value = UnwrapJsonElement(rawValue);

        return condition.Match switch
        {
            MatchString m => value is string s && s == m.Value,
            MatchInt m => value is long l && l == m.Value
                        || value is int i && i == m.Value
                        || value is ulong ul && ul == (ulong)m.Value,
            MatchDouble m => value is double d && Math.Abs(d - m.Value) < 1e-6
                          || value is float f && Math.Abs(f - m.Value) < 1e-6,
            MatchBool m => value is bool b && b == m.Value,
            _ => false
        };
    }

    private static bool EvaluateRange(RangeCondition condition, Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue(condition.Key, out var rawValue))
            return false;

        var unwrapped = UnwrapJsonElement(rawValue);

        long value;
        if (unwrapped is long l) value = l;
        else if (unwrapped is int i) value = i;
        else if (unwrapped is ulong ul && ul <= long.MaxValue) value = (long)ul;
        else return false; // Typ-Mismatch → false

        var r = condition.Range;

        if (r.Gt.HasValue && value <= r.Gt.Value) return false;
        if (r.Gte.HasValue && value < r.Gte.Value) return false;
        if (r.Lt.HasValue && value >= r.Lt.Value) return false;
        if (r.Lte.HasValue && value > r.Lte.Value) return false;

        return true;
    }

    private static bool EvaluateGeoRadius(GeoRadiusCondition condition, Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue(condition.Key, out var rawValue))
            return false;

        var value = UnwrapJsonElement(rawValue);

        // Erwartet: {"lat": 52.5, "lon": 13.4} oder [52.5, 13.4]
        double? lat = null;
        double? lon = null;

        if (value is Dictionary<string, object> dict)
        {
            if (dict.TryGetValue("lat", out var rawLat))
                lat = rawLat is JsonElement e1 ? e1.GetDouble() : Convert.ToDouble(rawLat);
            if (dict.TryGetValue("lon", out var rawLon))
                lon = rawLon is JsonElement e2 ? e2.GetDouble() : Convert.ToDouble(rawLon);
        }
        else if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() >= 2)
            {
                lat = element[0].GetDouble();
                lon = element[1].GetDouble();
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("lat", out var latProp))
                    lat = latProp.GetDouble();
                if (element.TryGetProperty("lon", out var lonProp))
                    lon = lonProp.GetDouble();
            }
        }

        if (!lat.HasValue || !lon.HasValue)
            return false;

        return HaversineDistance(condition.Lat, condition.Lon, lat.Value, lon.Value) <= condition.RadiusMeters;
    }

    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadius = 6371000.0; // Meter
        double dLat = ToRadians(lat2 - lat1);
        double dLon = ToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadius * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static bool EvaluateFullText(FullTextCondition condition, Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue(condition.Key, out var rawValue))
            return false;

        var value = UnwrapJsonElement(rawValue);
        if (value is not string text)
            return false;

        var query = FullTextQueryParser.Parse(condition.Query, condition.NotQuery);
        if (!query.HasPositiveClauses)
            return false;

        var document = FullTextQueryParser.BuildDocumentTerms(text);
        return FullTextQueryParser.Matches(document, query, condition.Mode);
    }

    /// <summary>
    /// System.Text.Json deserialisiert Dictionary<string, object> mit JsonElement-Werten.
    /// Diese Hilfsmethode wandelt JsonElement in den entsprechenden .NET-Typ um.
    /// </summary>
    private static object UnwrapJsonElement(object value)
    {
        if (value is not JsonElement element)
            return value;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => value
        };
    }
}
