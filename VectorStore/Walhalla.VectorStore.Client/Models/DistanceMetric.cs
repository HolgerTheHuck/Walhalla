// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Walhalla.VectorStore.Client.Models;

/// <summary>
/// Distanzmetrik fuer Vektor-Suche.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DistanceMetric
{
    Euclidean,
    Cosine,
    DotProduct
}
