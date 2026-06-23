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
    private readonly object _sync = new object();

    public InMemoryStore()
    {
        _dict = new Dictionary<byte[], byte[]>(ByteArrayComparer.Instance);
        _sortedKeys = new List<byte[]>();
    }

    public bool TryGet(byte[] key, out byte[]? value)
    {
        lock (_sync)
            return _dict.TryGetValue(key, out value);
    }

    public void Upsert(byte[] key, byte[] value)
    {
        lock (_sync)
        {
            _dict[key] = value;
            _sortedKeysDirty = true;
        }
    }

    public void Delete(byte[] key)
    {
        lock (_sync)
        {
            _dict.Remove(key);
            _sortedKeysDirty = true;
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
    {
        List<KeyValuePair<byte[], byte[]>> snapshot;
        lock (_sync)
        {
            EnsureSortedKeys();
            var comparer = ByteArrayComparer.Instance;
            snapshot = new List<KeyValuePair<byte[], byte[]>>();
            foreach (var key in _sortedKeys)
            {
                if (fromInclusive != null && comparer.Compare(key, fromInclusive) < 0)
                    continue;
                if (toExclusive != null && comparer.Compare(key, toExclusive) >= 0)
                    break;
                snapshot.Add(new KeyValuePair<byte[], byte[]>(key, _dict[key]));
            }
        }
        return new RangeEnumerable(snapshot);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        List<KeyValuePair<byte[], byte[]>> snapshot;
        lock (_sync)
        {
            EnsureSortedKeys();
            var comparer = ByteArrayComparer.Instance;
            snapshot = new List<KeyValuePair<byte[], byte[]>>();
            foreach (var key in _sortedKeys)
            {
                if (!key.AsSpan().StartsWith(prefix))
                {
                    if (comparer.Compare(key, prefix) > 0)
                        break;
                    continue;
                }
                snapshot.Add(new KeyValuePair<byte[], byte[]>(key, _dict[key]));
            }
        }
        return snapshot;
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
        lock (_sync)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                _dict[entries[i].Key] = entries[i].Value;
                _sortedKeysDirty = true;
            }
        }
    }

    public void BulkDelete(IReadOnlyList<byte[]> keys)
    {
        lock (_sync)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                _dict.Remove(keys[i]);
                _sortedKeysDirty = true;
            }
        }
    }

    public IStorageTransaction BeginTransaction(IsolationLevel isolation = IsolationLevel.Snapshot)
        => new DirectStorageTransaction(this);

    public IReadSnapshot BeginReadSnapshot()
        => new DirectReadSnapshot(this);

    public void Checkpoint() { }
    public Task CheckpointAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Vacuum() { }

    public StorageDiagnostics GetDiagnostics()
    {
        lock (_sync)
        {
            return new StorageDiagnostics { WalFileSizeBytes = 0, MemTableEntries = _dict.Count };
        }
    }

    public void Dispose() { }

    // ── interne Helpers (von TableStore genutzt) ───────────────────────────

    internal byte[] GetKeyAt(int index)
    {
        lock (_sync)
        {
            EnsureSortedKeys();
            return _sortedKeys[index];
        }
    }

    internal byte[] GetValueAt(int index)
    {
        lock (_sync)
        {
            EnsureSortedKeys();
            return _dict[_sortedKeys[index]];
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
                return _dict.Count;
        }
    }

    internal int IndexOfKey(byte[] key)
    {
        lock (_sync)
        {
            EnsureSortedKeys();
            return _sortedKeys.BinarySearch(key, ByteArrayComparer.Instance);
        }
    }

    private void EnsureSortedKeys()
    {
        if (!_sortedKeysDirty)
            return;

        _sortedKeys.Clear();
        foreach (var key in _dict.Keys)
            _sortedKeys.Add(key);
        _sortedKeys.Sort(ByteArrayComparer.Instance);
        _sortedKeysDirty = false;
    }

    private sealed class RangeEnumerable : IEnumerable<KeyValuePair<byte[], byte[]>>
    {
        private readonly List<KeyValuePair<byte[], byte[]>> _entries;

        public RangeEnumerable(List<KeyValuePair<byte[], byte[]>> entries)
        {
            _entries = entries;
        }

        public IEnumerator<KeyValuePair<byte[], byte[]>> GetEnumerator()
            => _entries.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => _entries.GetEnumerator();
    }
}
