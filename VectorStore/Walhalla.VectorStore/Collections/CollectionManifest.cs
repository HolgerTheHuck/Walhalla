using System;

namespace Walhalla.VectorStore.Collections;

/// <summary>
/// Persistiertes Collection-Manifest fuer Indexzustand und Reopen-Metadaten.
/// </summary>
public sealed class CollectionManifest
{
    public const int CurrentManifestVersion = 1;
    public const int CurrentPayloadIndexVersion = 3;
    public const int CurrentChangeStreamVersion = 1;

    public string Name { get; set; } = string.Empty;

    public int ManifestVersion { get; set; } = CurrentManifestVersion;

    public int PayloadIndexVersion { get; set; } = CurrentPayloadIndexVersion;

    public int ChangeStreamVersion { get; set; } = CurrentChangeStreamVersion;

    public bool PayloadIndexEnabled { get; set; }

    public bool PayloadIndexWarm { get; set; }

    public bool HasMatchIndexData { get; set; }

    public bool HasRangeIndexData { get; set; }

    public bool HasFullTextIndexData { get; set; }

    public bool HasGeoIndexData { get; set; }

    public bool PersistentMatch { get; set; }

    public bool PersistentRange { get; set; }

    public bool PersistentFullText { get; set; }

    public bool PersistentGeo { get; set; }

    public string? PayloadIndexStoragePath { get; set; }

    public long ChangeSequence { get; set; }

    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    public CollectionManifest Clone()
    {
        return new CollectionManifest
        {
            Name = Name,
            ManifestVersion = ManifestVersion,
            PayloadIndexVersion = PayloadIndexVersion,
            ChangeStreamVersion = ChangeStreamVersion,
            PayloadIndexEnabled = PayloadIndexEnabled,
            PayloadIndexWarm = PayloadIndexWarm,
            HasMatchIndexData = HasMatchIndexData,
            HasRangeIndexData = HasRangeIndexData,
            HasFullTextIndexData = HasFullTextIndexData,
            HasGeoIndexData = HasGeoIndexData,
            PersistentMatch = PersistentMatch,
            PersistentRange = PersistentRange,
            PersistentFullText = PersistentFullText,
            PersistentGeo = PersistentGeo,
            PayloadIndexStoragePath = PayloadIndexStoragePath,
            ChangeSequence = ChangeSequence,
            LastUpdatedUtc = LastUpdatedUtc
        };
    }
}