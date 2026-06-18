namespace WTreeModern.Tree;

/// <summary>
/// Delegaten für (De-)Serialisierung generischer Schlüssel und Werte.
/// Werden einmal beim Erstellen des WTree übergeben.
/// </summary>
public record Serializers<TKey, TValue>(
    Action<BinaryWriter, TKey> WriteKey,
    Func<BinaryReader, TKey>   ReadKey,
    Action<BinaryWriter, TValue> WriteValue,
    Func<BinaryReader, TValue>   ReadValue,
    Func<TValue, long?>?   TryGetLargeValueHandle = null,
    Func<long, TValue>?   CreateLargeValueHandle = null
)
{
    // Fertige Varianten für häufige Typen ─────────────────────────────────

    public static Serializers<string, string> StringString => new(
        (w, k) => w.Write(k),   r => r.ReadString(),
        (w, v) => w.Write(v),   r => r.ReadString()
    );

    public static Serializers<long, long> LongLong => new(
        (w, k) => w.Write(k),   r => r.ReadInt64(),
        (w, v) => w.Write(v),   r => r.ReadInt64()
    );

    public static Serializers<string, long> StringLong => new(
        (w, k) => w.Write(k),   r => r.ReadString(),
        (w, v) => w.Write(v),   r => r.ReadInt64()
    );

    public static Serializers<int, string> IntString => new(
        (w, k) => w.Write(k),   r => r.ReadInt32(),
        (w, v) => w.Write(v),   r => r.ReadString()
    );
}
