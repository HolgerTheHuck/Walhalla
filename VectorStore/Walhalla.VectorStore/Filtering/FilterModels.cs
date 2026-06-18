// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Walhalla.Indexes.FullText;

namespace Walhalla.VectorStore.Filtering;

/// <summary>Root-Knoten eines Filter-AST.</summary>
public sealed record FilterClause(Condition[] Must, Condition[]? Should, Condition[]? MustNot);

/// <summary>Basisklasse fuer eine einzelne Bedingung.</summary>
public abstract record Condition;

/// <summary>Match-Bedingung: key muss exakt zu einem Wert passen.</summary>
public sealed record MatchCondition(string Key, MatchValue Match) : Condition;

/// <summary>Range-Bedingung: key muss in einen numerischen Bereich fallen.</summary>
public sealed record RangeCondition(string Key, RangeValue Range) : Condition;

/// <summary>Geo-Radius-Bedingung: key muss einen Punkt innerhalb eines Radius um (lat, lon) enthalten.</summary>
public sealed record GeoRadiusCondition(string Key, double Lat, double Lon, double RadiusMeters) : Condition;

/// <summary>Full-Text-Bedingung mit all/any und optionalem Ausschluss-Query.</summary>
public sealed record FullTextCondition(string Key, string Query, FullTextQueryMode Mode = FullTextQueryMode.All, string? NotQuery = null) : Condition;

/// <summary>Basisklasse fuer Match-Werte.</summary>
public abstract record MatchValue;

public sealed record MatchString(string Value) : MatchValue;
public sealed record MatchInt(long Value) : MatchValue;
public sealed record MatchDouble(double Value) : MatchValue;
public sealed record MatchBool(bool Value) : MatchValue;

/// <summary>Numerischer Bereich (nur Integer, da Payload als long/double gespeichert wird).</summary>
public sealed record RangeValue(long? Gt, long? Gte, long? Lt, long? Lte);

/// <summary>Ausnahme bei ungueltiger Filter-Syntax.</summary>
public sealed class FilterParseException : Exception
{
    public FilterParseException(string message) : base(message) { }
}
