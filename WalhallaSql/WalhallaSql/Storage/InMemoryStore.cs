using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql.Core;
using Walhalla.Storage.Contract;

namespace WalhallaSql.Storage;

internal sealed class InMemoryStore : IKeyValueStore
{
    private readonly Dictionary<byte[], byte[]> _dict;
    private readonly List<byte[]> _sortedKeys;
    private bool _sortedKeysDirty;

    public InMemoryStore()
    {
        _dict = new Dictionary<byte[], byte[]>(ByteArrayComparer.Instance);
        _sortedKeys = new List<byte[]>();
    }

    public bool TryGet(byte[] key, out byte[]? value)
        => _dict.TryGetValue(key, out value);

    public void Upsert(byte[] key, byte[] value)
    {
        _dict[key] = value;
        _sortedKeysDirty = true;
    }

    public void Delete(byte[] key)
    {
        _dict.Remove(key);
        _sortedKeysDirty = true;
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
    {
        EnsureSortedKeys();
        return new RangeEnumerable(_dict, _sortedKeys, fromInclusive, toExclusive);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        EnsureSortedKeys();
        var comparer = ByteArrayComparer.Instance;
        foreach (var key in _sortedKeys)
        {
            if (!key.AsSpan().StartsWith(prefix))
            {
                // Da sortiert: sobald Key > prefix-Prefix-Bereich, können wir abbrechen.
                // Einfacher Abbruch, wenn Key > prefix (lexikografisch).
                if (comparer.Compare(key, prefix) > 0)
                    break;
                continue;
            }
            yield return new KeyValuePair<byte[], byte[]>(key, _dict[key]);
        }
    }

    public void ScanValues(byte[]? fromInclusive, byte[]? toExclusive,
        Func<byte[], int, int, bool> action)
    {
        foreach (var kv in Scan(fromInclusive, toExclusive))
        {
            if (!action(kv.Value, 0, kv.Value.Length))
                break;
        }
    }

    public void BulkUpsert(IReadOnlyList<KeyValuePair<byte[], byte[]>> entries)
    {
        for (int i = 0; i < entries.Count; i++)
            Upsert(entries[i].Key, entries[i].Value);
    }

    public void BulkDelete(IReadOnlyList<byte[]> keys)
    {
        for (int i = 0; i < keys.Count; i++)
            Delete(keys[i]);
    }

    public IStorageTransaction BeginTransaction(IsolationLevel isolation = IsolationLevel.Snapshot)
        => new DirectStorageTransaction(this);

    public IReadSnapshot BeginReadSnapshot()
        => new DirectReadSnapshot(this);

    public void Checkpoint() { }
    public Task CheckpointAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Vacuum() { }

    public StorageDiagnostics GetDiagnostics()
        => new StorageDiagnostics { WalFileSizeBytes = 0, MemTableEntries = _dict.Count };

    public void Dispose() { }

    // ── interne Helpers (von TableStore genutzt) ───────────────────────────

    internal byte[] GetKeyAt(int index)
    {
        EnsureSortedKeys();
        return _sortedKeys[index];
    }

    internal byte[] GetValueAt(int index)
    {
        EnsureSortedKeys();
        return _dict[_sortedKeys[index]];
    }

    internal int Count => _dict.Count;

    internal int IndexOfKey(byte[] key)
    {
        EnsureSortedKeys();
        return _sortedKeys.BinarySearch(key, ByteArrayComparer.Instance);
    }

    private void EnsureSortedKeys()
    {
        if (!_sortedKeysDirty) return;

        _sortedKeys.Clear();
        foreach (var key in _dict.Keys)
            _sortedKeys.Add(key);
        _sortedKeys.Sort(ByteArrayComparer.Instance);
        _sortedKeysDirty = false;
    }

    private sealed class RangeEnumerable : IEnumerable<KeyValuePair<byte[], byte[]>>
    {
        private readonly Dictionary<byte[], byte[]> _dict;
        private readonly List<byte[]> _sortedKeys;
        private readonly byte[]? _from;
        private readonly byte[]? _to;

        public RangeEnumerable(Dictionary<byte[], byte[]> dict, List<byte[]> sortedKeys,
            byte[]? from, byte[]? to)
        {
            _dict = dict;
            _sortedKeys = sortedKeys;
            _from = from;
            _to = to;
        }

        public IEnumerator<KeyValuePair<byte[], byte[]>> GetEnumerator()
            => new RangeEnumerator(_dict, _sortedKeys, _from, _to);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class RangeEnumerator : IEnumerator<KeyValuePair<byte[], byte[]>>
    {
        private readonly Dictionary<byte[], byte[]> _dict;
        private readonly List<byte[]> _sortedKeys;
        private readonly byte[]? _to;
        private readonly int _count;
        private int _index;
        private KeyValuePair<byte[], byte[]> _current;

        public RangeEnumerator(Dictionary<byte[], byte[]> dict, List<byte[]> sortedKeys,
            byte[]? from, byte[]? to)
        {
            _dict = dict;
            _sortedKeys = sortedKeys;
            _to = to;
            _count = sortedKeys.Count;

            if (from != null && _count > 0)
            {
                var idx = sortedKeys.BinarySearch(from, ByteArrayComparer.Instance);
                _index = idx >= 0 ? idx : ~idx;
            }
            else
            {
                _index = 0;
            }

            _current = default;
        }

        public KeyValuePair<byte[], byte[]> Current => _current;

        object System.Collections.IEnumerator.Current => _current;

        public bool MoveNext()
        {
            if (_index >= _count)
                return false;

            var key = _sortedKeys[_index];
            if (_to != null && ByteArrayComparer.Instance.Compare(key, _to) >= 0)
                return false;

            _current = new KeyValuePair<byte[], byte[]>(key, _dict[key]);
            _index++;
            return true;
        }

        public void Reset() => _index = 0;
        public void Dispose() { }
    }
}
