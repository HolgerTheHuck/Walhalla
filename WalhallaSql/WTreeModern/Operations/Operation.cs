namespace WTreeModern.Operations;

/// <summary>
/// Eine einzelne, unveränderliche Schreiboperation.
/// value ist nur bei Upsert relevant.
/// Sequence wird für MVCC-Versionierung verwendet (0 = Legacy).
/// </summary>
public readonly struct Operation<TKey, TValue>
{
    public readonly ulong Sequence;
    public readonly OperationType Type;
    public readonly TKey Key;

    // Nullable, damit kein Default-Wert für TValue nötig ist
    public readonly TValue? Value;

    public Operation(OperationType type, TKey key, TValue? value = default, ulong sequence = 0)
    {
        Sequence = sequence;
        Type     = type;
        Key      = key;
        Value    = value;
    }

    public override string ToString() =>
        Type == OperationType.Upsert
            ? $"Upsert({Key} = {Value}) @ {Sequence}"
            : $"Delete({Key}) @ {Sequence}";
}
