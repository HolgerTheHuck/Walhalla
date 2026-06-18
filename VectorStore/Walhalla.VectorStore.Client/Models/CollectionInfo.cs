// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.VectorStore.Client.Models;

/// <summary>
/// Metadaten einer Collection.
/// </summary>
public class CollectionInfo
{
    public required string Name { get; set; }
    public int Dimension { get; set; }
    public DistanceMetric Metric { get; set; }
    public int Count { get; set; }
    public bool HnswEnabled { get; set; }
}
