using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RimWorldCodeRag.Retrieval;

/// <summary>
/// LRU cache of finished result lists, keyed by the full request shape
/// (<c>query + kind + max + candidates + fusion + weights</c>).
///
/// <para>
/// The embedding cache only saves the query vector; this saves the whole search, which matters for
/// an agent that retries the same query with slightly different phrasing or repeats a lookup.
/// Callers get the same (immutable-by-convention) result instances back, so nothing may mutate them.
/// </para>
/// </summary>
internal sealed class ResultCache
{
    private readonly int _maxSize;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lruList = new();
    private readonly object _lock = new();

    public ResultCache(int maxSize = 200)
    {
        _maxSize = Math.Max(1, maxSize);
    }

    public int Count => _cache.Count;

    public bool TryGet(string key, out IReadOnlyList<RoughSearchResult> results)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            lock (_lock)
            {
                _lruList.Remove(entry.Node);
                _lruList.AddFirst(entry.Node);
            }

            results = entry.Results;
            return true;
        }

        results = Array.Empty<RoughSearchResult>();
        return false;
    }

    public void Add(string key, IReadOnlyList<RoughSearchResult> results)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _lruList.Remove(existing.Node);
                _lruList.AddFirst(existing.Node);
                _cache[key] = new CacheEntry(results, existing.Node);
                return;
            }

            if (_cache.Count >= _maxSize)
            {
                var oldest = _lruList.Last;
                if (oldest != null)
                {
                    _lruList.RemoveLast();
                    _cache.TryRemove(oldest.Value, out _);
                }
            }

            var node = _lruList.AddFirst(key);
            _cache[key] = new CacheEntry(results, node);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
        }
    }

    private readonly record struct CacheEntry(IReadOnlyList<RoughSearchResult> Results, LinkedListNode<string> Node);
}
