using System.Collections.Generic;

namespace FctAggregator;

/// <summary>
/// 轻量级 LRU (Least Recently Used) 缓存包装器（v3.21.1 P1）。
/// 替换无限增长的 Dictionary，防止内存泄漏。
/// 支持：最大容量限制 + TTL 过期自动淘汰。
/// </summary>
/// <typeparam name="TKey">键类型</typeparam>
/// <typeparam name="TValue">值类型</typeparam>
public class LRUCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, CacheEntry> _cache = new();
    private readonly LinkedList<TKey> _order = new();  // 按访问/写入时间排序，头最新尾最旧
    private readonly int _maxSize;
    private readonly TimeSpan _ttl;
    private readonly object _lock = new();

    private sealed class CacheEntry
    {
        public TValue Value = default!;
        public DateTime LastAccessed;
        public DateTime CreatedAt;
    }

    /// <summary>
    /// 构造 LRU 缓存。
    /// </summary>
    /// <param name="maxSize">最大容量（默认 512）</param>
    /// <param name="ttl">条目过期时间（默认 7 天）</param>
    public LRUCache(int maxSize = 512, TimeSpan ttl = default)
    {
        _maxSize = Math.Max(1, maxSize);
        _ttl = ttl == default ? TimeSpan.FromDays(7) : ttl;
        
        if (_ttl.TotalHours < 1)
            Logger.Warning($"[LRUCache] TTL 设置过短 ({_ttl.TotalHours:F1}小时)，可能导致频繁淘汰");
    }

    /// <summary>获取或设置键的值。若不存在且需添加 → 先淘汰最旧项。</summary>
    public TValue? GetOrSet(TKey key, Func<TValue> factory)
    {
        lock (_lock)
        {
            TryPruneExpired();
            
            if (_cache.TryGetValue(key, out var entry))
            {
                // 命中：更新访问时间，调整到链表头部
                entry.LastAccessed = DateTime.Now;
                MoveToHead(key);
                return entry.Value;
            }
            
            // 未命中：创建新项
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

    /// <summary>直接 Set 值（不调用 factory）。</summary>
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

    /// <summary>尝试 Prune（自动淘汰过期条目）。</summary>
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
                _order.Remove(k); // 修复：按 key 精确移除（RemoveFirst 会误删最新条目）
            }
            
            if (toRemove.Count > 0)
                Logger.Debug($"[LRUCache] 淘汰 {toRemove.Count} 个过期条目，当前大小 {_cache.Count}");
        }
    }

    /// <summary>淘汰最旧的条目（链表尾部）。</summary>
    private void EvictOldest()
    {
        if (_order.Count == 0) return;
        
        var oldestNode = _order.Last;
        var oldestKey = oldestNode!.Value;
        
        _order.RemoveLast();
        _cache.Remove(oldestKey);
        
        Logger.Debug($"[LRUCache] 淘汰最旧条目：{oldestKey}");
    }

    /// <summary>将键移动到链表头部（标记为最新访问）。</summary>
    private void MoveToHead(TKey key)
    {
        var node = FindNode(key);
        if (node != null && node != _order.First)
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
    }

    /// <summary>在链表中查找节点（O(n) 简单实现，对小规模缓存性能可接受）。</summary>
    private LinkedListNode<TKey>? FindNode(TKey key)
    {
        var curr = _order.First;
        while (curr != null)
        {
            // 修复：比较的是链表节点存的 key 本身（原版误比 CacheEntry 对象）
            if (EqualityComparer<TKey>.Default.Equals(curr.Value, key))
                return curr;
            curr = curr.Next;
        }
        return null;
    }

    /// <summary>清除所有条目。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _order.Clear();
            Logger.Info("[LRUCache] 手动清除缓存");
        }
    }

    /// <summary>当前缓存大小（不含即将淘汰的）。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _cache.Count;
        }
    }
}
