namespace Walhalla.VectorStore.Client.Models;

public sealed class CollectionManifest
{
    public string Name { get; set; } = string.Empty;

    public int ManifestVersion { get; set; }

    public int PayloadIndexVersion { get; set; }

    public int ChangeStreamVersion { get; set; }

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

    public DateTime LastUpdatedUtc { get; set; }
}