using System.Collections.Generic;

namespace FctAggregator;

public class LRUCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, CacheEntry> _cache = new();
    private readonly LinkedList<TKey> _order = new();
    private readonly int _maxSize;
    private readonly TimeSpan _ttl;
    private readonly object _lock = new();

    private sealed class CacheEntry
    {
        public TValue Value = default!;
        public DateTime LastAccessed;
        public DateTime CreatedAt;
    }

    public LRUCache(int maxSize = 512, TimeSpan ttl = default)
    {
        _maxSize = Math.Max(1, maxSize);
        _ttl = ttl == default ? TimeSpan.FromDays(7) : ttl;

        if (_ttl.TotalHours < 1)
            Logger.Warning($"[LRUCache] TTL 设置过短 ({_ttl.TotalHours:F1}小时)，可能导致频繁淘汰");
    }

    public TValue? GetOrSet(TKey key, Func<TValue> factory)
    {
        lock (_lock)
        {
            TryPruneExpired();

            if (_cache.TryGetValue(key, out var entry))
            {
                entry.LastAccessed = DateTime.Now;
                MoveToHead(key);
                return entry.Value;
            }

            if (_cache.Count >= _maxSize)
                EvictOldest();

            var newValue = factory();
            var newEntry = new CacheEntry
            {
                Value = newValue,
                CreatedAt = DateTime.Now,
                LastAccessed = DateTime.Now
            };

            _cache[key] = newEntry;
            _order.AddFirst(key);
            return newValue;
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            TryPruneExpired();
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.LastAccessed = DateTime.Now;
                MoveToHead(key);
                value = entry.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            TryPruneExpired();

            if (_cache.ContainsKey(key))
            {
                var entry = _cache[key];
                entry.Value = value;
                entry.LastAccessed = DateTime.Now;
                MoveToHead(key);
            }
            else
            {
                if (_cache.Count >= _maxSize)
                    EvictOldest();

                var newEntry = new CacheEntry
                {
                    Value = value,
                    CreatedAt = DateTime.Now,
                    LastAccessed = DateTime.Now
                };

                _cache[key] = newEntry;
                _order.AddFirst(key);
            }
        }
    }

    public void TryPruneExpired()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var toRemove = new List<TKey>();

            foreach (var kvp in _cache)
            {
                if (now - kvp.Value.CreatedAt > _ttl)
                    toRemove.Add(kvp.Key);
            }

            foreach (var k in toRemove)
            {
                _cache.Remove(k);
                _order.Remove(k);
            }

            if (toRemove.Count > 0)
                Logger.Debug($"[LRUCache] 淘汰 {toRemove.Count} 个过期条目，当前大小 {_cache.Count}");
        }
    }

    private void EvictOldest()
    {
        if (_order.Count == 0) return;

        var oldestNode = _order.Last;
        var oldestKey = oldestNode!.Value;

        _order.RemoveLast();
        _cache.Remove(oldestKey);

        Logger.Debug($"[LRUCache] 淘汰最旧条目：{oldestKey}");
    }

    private void MoveToHead(TKey key)
    {
        var node = FindNode(key);
        if (node != null && node != _order.First)
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
    }

    private LinkedListNode<TKey>? FindNode(TKey key)
    {
        var curr = _order.First;
        while (curr != null)
        {
            if (EqualityComparer<TKey>.Default.Equals(curr.Value, key))
                return curr;
            curr = curr.Next;
        }
        return null;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _order.Clear();
            Logger.Info("[LRUCache] 手动清除缓存");
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _cache.Count;
        }
    }
}
