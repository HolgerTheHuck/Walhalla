using WTreeModern.Diagnostics;

namespace WTreeModern.Tree;

/// <summary>
/// Einfacher LRU-Cache für Baumknoten (Handle → Knotenobjekt).
///
/// Eviction-Strategie:
///   Knoten werden vom LRU-Ende (kälteste) evicted, sofern sie NICHT dirty sind.
///   Dirty Knoten bleiben solange im Cache, bis sie durch SaveDirtyNodes()
///   geflusht und als nicht-dirty markiert wurden.
///   Dadurch kann der Cache bei ausschließlich dirty Knoten kurzfristig
///   über die Kapazitätsgrenze wachsen – das ist korrekt und gewollt.
/// </summary>
internal sealed class LruNodeCache
{
    // ── Interner Eintrag ─────────────────────────────────────────────────────

    private readonly record struct Entry(long Handle, object Node);

    // ── Felder ───────────────────────────────────────────────────────────────

    private readonly int                                       _capacity;
    private readonly Func<object, bool>                        _isDirty;
    private readonly ITelemetry?                                 _telemetry;
    private readonly Dictionary<long, LinkedListNode<Entry>>   _map  = new();
    private readonly LinkedList<Entry>                         _list = new();
    private readonly object                                  _lock = new();

    // ── Konstruktor ──────────────────────────────────────────────────────────

    /// <param name="capacity">Maximale Anzahl Einträge vor der Eviction.</param>
    /// <param name="isDirty">Callback: gibt true zurück, wenn der Knoten noch nicht persistiert ist.</param>
    /// <param name="telemetry">Optionale Telemetrie für Cache-Metriken.</param>
    public LruNodeCache(int capacity, Func<object, bool> isDirty, ITelemetry? telemetry = null)
    {
        _capacity  = capacity;
        _isDirty   = isDirty;
        _telemetry = telemetry;
    }

    // ── Öffentliche API ──────────────────────────────────────────────────────

    /// <summary>Anzahl der aktuell gecachten Knoten.</summary>
    public int Count
    {
        get
        {
            lock (_lock) { return _map.Count; }
        }
    }

    /// <summary>
    /// Liest einen Knoten aus dem Cache und markiert ihn als zuletzt verwendet (MRU).
    /// Gibt false zurück, wenn der Handle nicht im Cache ist.
    /// </summary>
    public bool TryGetValue(long handle, out object? node)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(handle, out var listNode))
            {
                node = null;
                _telemetry?.IncrementCounter("cache_misses");
                return false;
            }

            // Zum MRU-Ende verschieben (außer wenn bereits vorne)
            if (listNode != _list.First)
            {
                _list.Remove(listNode);
                _list.AddFirst(listNode);
            }

            node = listNode.Value.Node;
            _telemetry?.IncrementCounter("cache_hits");
            return true;
        }
    }

    /// <summary>
    /// Legt einen Knoten im Cache ab (fügt ein oder aktualisiert).
    /// Anschließend werden ggf. clean Knoten am LRU-Ende evicted.
    /// </summary>
    public void Set(long handle, object node)
    {
        lock (_lock)
        {
            bool existed = _map.TryGetValue(handle, out var existing);
            if (existed)
            {
                _list.Remove(existing!);
                _map.Remove(handle);
            }

            var listNode = new LinkedListNode<Entry>(new Entry(handle, node));
            _list.AddFirst(listNode);
            _map[handle] = listNode;

            if (!existed)
                _telemetry?.IncrementCounter("cache_inserts");

            Evict();
        }
    }

    /// <summary>Entfernt einen Knoten explizit aus dem Cache.</summary>
    public void Remove(long handle)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(handle, out var listNode))
            {
                _list.Remove(listNode);
                _map.Remove(handle);
            }
        }
    }

    /// <summary>Leert den gesamten Cache.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _list.Clear();
        }
    }

    /// <summary>
    /// Liefert alle gecachten (Handle, Node)-Paare – Momentaufnahme,
    /// sicher gegenüber Modifikationen während der Iteration.
    /// </summary>
    public IEnumerable<(long Handle, object Node)> AllNodes()
    {
        lock (_lock)
        {
            foreach (var entry in _list)
                yield return (entry.Handle, entry.Node);
        }
    }

    // ── Eviction ─────────────────────────────────────────────────────────────

    // Wird nur von Set() aufgerufen, die bereits _lock hält.
    private void Evict()
    {
        var current = _list.Last;
        while (_map.Count > _capacity && current != null)
        {
            var prev = current.Previous;
            if (!_isDirty(current.Value.Node))
            {
                if (current.Value.Node is INode n && n.IsPinned)
                {
                    _telemetry?.IncrementCounter("cache_eviction_dirty_skipped");
                    current = prev;
                    continue;
                }
                _map.Remove(current.Value.Handle);
                _list.Remove(current);
                _telemetry?.IncrementCounter("cache_evictions");
            }
            current = prev;
        }
    }
}
