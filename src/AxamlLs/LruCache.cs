namespace AxamlLs;

/// <summary>
/// A small thread-safe least-recently-used cache (hash map + doubly linked list: O(1) get, add and eviction),
/// bounded both by entry count and by a total weight such as the size in characters.
/// </summary>
public sealed class LruCache<TKey, TValue>(int maxEntries, long maxWeight) where TKey : notnull
{
    private sealed record Item(TKey Key, TValue Value, long Weight);

    private readonly Dictionary<TKey, LinkedListNode<Item>> _map = [];
    private readonly LinkedList<Item> _order = new();
    private readonly object _gate = new();
    private long _weight;

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value, long weight)
    {
        if (weight > maxWeight) return; // would evict everything else
        lock (_gate)
        {
            if (_map.Remove(key, out var old))
            {
                _order.Remove(old);
                _weight -= old.Value.Weight;
            }
            _map[key] = _order.AddFirst(new Item(key, value, weight));
            _weight += weight;
            while (_map.Count > maxEntries || _weight > maxWeight)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _weight -= last.Value.Weight;
            }
        }
    }
}
