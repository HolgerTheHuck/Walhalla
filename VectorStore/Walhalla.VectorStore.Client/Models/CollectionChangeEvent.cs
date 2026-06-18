namespace Walhalla.VectorStore.Client.Models;

public sealed class CollectionChangeEvent
{
    public long Sequence { get; set; }

    public string Collection { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public DateTime TimestampUtc { get; set; }

    public List<CollectionChangeItem> Items { get; set; } = new();
}

public sealed class CollectionChangeItem
{
    public ulong Id { get; set; }

    public float[]? Vector { get; set; }

    public Dictionary<string, object>? Payload { get; set; }
}