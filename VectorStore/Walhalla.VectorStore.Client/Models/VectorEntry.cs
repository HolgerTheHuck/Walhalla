// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.VectorStore.Client.Models;

/// <summary>
/// Ein gespeicherter Vektor mit optionalen Metadaten.
/// </summary>
public class VectorEntry
{
    public ulong Id { get; set; }
    public int Dimension { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
