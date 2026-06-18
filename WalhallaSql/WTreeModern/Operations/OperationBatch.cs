namespace WTreeModern.Operations;

/// <summary>
/// Geordnete Menge von Operationen für einen Branch-Puffer.
///
/// Kompaktierung (Compaction):
///   Für denselben Key gewinnt immer die neueste Operation.
///   Damit schrumpft der Puffer, bevor er nach unten propagiert wird.
///   Beispiel:
///     Upsert(k,1), Upsert(k,2), Delete(k) → Delete(k)
///     Delete(k), Upsert(k,3)              → Upsert(k,3)
/// </summary>
public sealed class OperationBatch<TKey, TValue>
    where TKey : notnull
{
    private readonly List<Operation<TKey, TValue>> _ops = [];
    private readonly IComparer<TKey> _comparer;

    public int Count => _ops.Count;

    public OperationBatch(IComparer<TKey> comparer)
    {
        _comparer = comparer;
    }

    public void Add(Operation<TKey, TValue> op) => _ops.Add(op);

    public void AddRange(IEnumerable<Operation<TKey, TValue>> ops) => _ops.AddRange(ops);

    /// <summary>
    /// Extrahiert alle Operationen, deren Key in [fromKey, toKey] liegt,
    /// und entfernt sie aus diesem Batch (für den Fall-Mechanismus).
    /// Null-Grenzen bedeuten "unbegrenzt".
    /// </summary>
    public List<Operation<TKey, TValue>> ExtractRange(TKey? fromKey, TKey? toKey)
    {
        var extracted = new List<Operation<TKey, TValue>>();
        var remaining = new List<Operation<TKey, TValue>>();

        foreach (var op in _ops)
        {
            bool inRange =
                (fromKey is null || _comparer.Compare(op.Key, fromKey) >= 0) &&
                (toKey   is null || _comparer.Compare(op.Key, toKey)   <= 0);

            if (inRange)
                extracted.Add(op);
            else
                remaining.Add(op);
        }

        _ops.Clear();
        _ops.AddRange(remaining);
        return extracted;
    }

    public void Clear() => _ops.Clear();

    public IReadOnlyList<Operation<TKey, TValue>> All => _ops;

    /// <summary>
    /// Durchsucht den Puffer rückwärts (neueste zuerst) nach einer
    /// Operation für den angegebenen Key.
    /// </summary>
    public bool TryScanForKey(TKey key, out Operation<TKey, TValue> op)
    {
        for (int i = _ops.Count - 1; i >= 0; i--)
        {
            if (_comparer.Compare(_ops[i].Key, key) == 0)
            {
                op = _ops[i];
                return true;
            }
        }
        op = default;
        return false;
    }
}
