// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;

namespace Walhalla.VectorStore.Collections;

/// <summary>
/// Point-in-Time Snapshot für konsistente Scans über Collections.
/// </summary>
/// <remarks>
/// Hält Sequenznummern aller Collections fest zum Zeitpunkt der Erstellung.
/// Liefert nur Daten, die zum Snapshot-Zeitpunkt committed waren.
/// </remarks>
public sealed class Snapshot : IDisposable
{
    private readonly IKeyValueStore _store;
    private readonly Dictionary<string, long> _sequenceNumbers;
    private readonly string[] _collectionNames;
    private readonly long _createdAt;
    private bool _disposed;

    public long Timestamp => _createdAt;
    public IReadOnlyList<string> CollectionNames => _collectionNames;

    internal Snapshot(IKeyValueStore store, Dictionary<string, long> sequences, string[] names)
    {
        _store = store;
        _sequenceNumbers = sequences;
        _collectionNames = names;
        _createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Prüft, ob eine Collection zum Snapshot-Zeitpunkt existierte.
    /// </summary>
    public bool ContainsCollection(string name) => _sequenceNumbers.ContainsKey(name);

    /// <summary>
    /// Erzeugt einen konsistenten Iterator über eine Collection.
    /// </summary>
    public SnapshotIterator CreateIterator(string collectionName, int dimension)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Snapshot));

        if (!_sequenceNumbers.TryGetValue(collectionName, out var sequence))
            throw new ArgumentException($"Collection '{collectionName}' not found in snapshot", nameof(collectionName));

        return new SnapshotIterator(_store, collectionName, dimension, sequence);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

/// <summary>
/// Iterator über Vektor-Daten zu einem bestimmten Snapshot.
/// </summary>
public sealed class SnapshotIterator : IAsyncEnumerable<VectorRecord>
{
    private readonly IKeyValueStore _store;
    private readonly string _collection;
    private readonly int _dimension;
    private readonly long _snapshotSequence;

    public SnapshotIterator(IKeyValueStore store, string collection, int dimension, long snapshotSequence)
    {
        _store = store;
        _collection = collection;
        _dimension = dimension;
        _snapshotSequence = snapshotSequence;
    }

    /// <summary>
    /// Gibt alle Records der Collection zurück (best effort).
    /// </summary>
    public async IAsyncEnumerator<VectorRecord> GetAsyncEnumerator(CancellationToken ct = default)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes($"c:{_collection}:v:");
        var entries = _store.ScanPrefix(prefix).ToList();

        foreach (var (key, vectorBytes) in entries)
        {
            var keyStr = System.Text.Encoding.UTF8.GetString(key);
            if (!TryParseId(keyStr, out var id)) continue;

            var vector = Vector.FromByteArray(vectorBytes, _dimension);

            // Metadaten laden
            var metaKey = System.Text.Encoding.UTF8.GetBytes($"c:{_collection}:m:{id}");
            var metadata = _store.TryGet(metaKey, out var metaBytes) && metaBytes is not null
                ? VectorMetadata.FromJsonBytes(metaBytes) : null;

            yield return new VectorRecord(id, vector, metadata, _snapshotSequence);
        }
    }

    private static bool TryParseId(string key, out ulong id)
    {
        id = 0;
        var lastColon = key.LastIndexOf(':');
        if (lastColon < 0 || lastColon == key.Length - 1) return false;
        return ulong.TryParse(key.AsSpan(lastColon + 1), out id);
    }
}

/// <summary>
/// Ein einzelner Vektor-Datensatz für Iteration.
/// </summary>
public sealed record VectorRecord(
    ulong Id,
    Vector Vector,
    VectorMetadata? Metadata,
    long SequenceNumber
);
