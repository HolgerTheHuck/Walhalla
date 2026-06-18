// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.VectorStore.Indexes;

/// <summary>
/// Optionen fuer den Payload-Index einer Collection.
/// </summary>
public sealed class PayloadIndexOptions
{
    /// <summary>Aktiviert persistente Match-Indizes.</summary>
    public bool PersistentMatch { get; set; }

    /// <summary>Aktiviert persistente Range-Indizes.</summary>
    public bool PersistentRange { get; set; }

    /// <summary>Aktiviert persistente Volltext-Indizes.</summary>
    public bool PersistentFullText { get; set; }

    /// <summary>Aktiviert persistente Geo-Snapshots.</summary>
    public bool PersistentGeo { get; set; }

    /// <summary>
    /// Optionaler Basis-Pfad fuer persistente Payload-Indizes. Wenn null, wird unterhalb des Hauptstores gespeichert.
    /// </summary>
    public string? StoragePath { get; set; }

    /// <summary>Maximale Knotenbreite fuer Geo-RTrees.</summary>
    public int GeoMaxEntries { get; set; } = 16;

    public bool HasPersistentBackends => PersistentMatch || PersistentRange || PersistentFullText || PersistentGeo;
}