namespace WTreeModern.Operations;

/// <summary>Eine Operation mit zugeordneter Commit-Sequence-Nummer (MVCC).</summary>
public readonly record struct VersionedOperation<TKey, TValue>(
    ulong Sequence,
    OperationType Type,
    TKey Key,
    TValue? Value
);
