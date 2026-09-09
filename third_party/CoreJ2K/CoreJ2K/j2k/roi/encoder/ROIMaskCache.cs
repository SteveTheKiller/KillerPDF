// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CoreJ2K.j2k.roi.encoder
{
    /// <summary>
    /// Caches computed ROI masks to improve performance.
    /// Uses a thread-safe cache with LRU eviction policy.
    /// </summary>
    public class ROIMaskCache
    {
        private readonly ConcurrentDictionary<ROIMaskKey, CachedMask> _cache;
        private readonly LinkedList<ROIMaskKey> _lruList;
        private readonly object _lruLock = new object();
        private readonly int _maxCacheSize;
        
        /// <summary>
        /// Gets the maximum number of masks that can be cached.
        /// </summary>
        public int MaxCacheSize => _maxCacheSize;
        
        /// <summary>
        /// Gets the current number of cached masks.
        /// </summary>
        public int Count => _cache.Count;
        
        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        public CacheStatistics Statistics { get; }
        
        /// <summary>
        /// Creates a new ROI mask cache.
        /// </summary>
        /// <param name="maxCacheSize">Maximum number of masks to cache (default: 100)</param>
        public ROIMaskCache(int maxCacheSize = 100)
        {
            if (maxCacheSize <= 0)
                throw new ArgumentException("Max cache size must be positive", nameof(maxCacheSize));
                
            _maxCacheSize = maxCacheSize;
            _cache = new ConcurrentDictionary<ROIMaskKey, CachedMask>();
            _lruList = new LinkedList<ROIMaskKey>();
            Statistics = new CacheStatistics();
        }
        
        /// <summary>
        /// Attempts to retrieve a cached mask.
        /// </summary>
        /// <param name="key">The cache key</param>
        /// <param name="mask">The cached mask data if found</param>
        /// <returns>True if mask was found in cache</returns>
        public bool TryGetMask(ROIMaskKey key, out int[] mask)
        {
            if (_cache.TryGetValue(key, out var cachedMask))
            {
                // Update LRU list
                lock (_lruLock)
                {
                    _lruList.Remove(cachedMask.ListNode);
                    cachedMask.ListNode = _lruList.AddFirst(key);
                }
                
                mask = cachedMask.MaskData;
                Statistics.RecordHit();
                return true;
            }
            
            mask = null;
            Statistics.RecordMiss();
            return false;
        }
        
        /// <summary>
        /// Adds a mask to the cache.
        /// </summary>
        /// <param name="key">The cache key</param>
        /// <param name="mask">The mask data to cache</param>
        public void AddMask(ROIMaskKey key, int[] mask)
        {
            if (mask == null)
                throw new ArgumentNullException(nameof(mask));
            
            // Make a defensive copy
            var maskCopy = new int[mask.Length];
            Array.Copy(mask, maskCopy, mask.Length);
            
            lock (_lruLock)
            {
                // Evict if necessary
                while (_cache.Count >= _maxCacheSize && _lruList.Last != null)
                {
                    var evictKey = _lruList.Last.Value;
                    _lruList.RemoveLast();
                    _cache.TryRemove(evictKey, out _);
                    Statistics.RecordEviction();
                }
                
                // Add to cache
                var listNode = _lruList.AddFirst(key);
                var cachedMask = new CachedMask
                {
                    MaskData = maskCopy,
                    ListNode = listNode
                };
                
                _cache[key] = cachedMask;
                Statistics.RecordAdd();
            }
        }
        
        /// <summary>
        /// Clears all cached masks.
        /// </summary>
        public void Clear()
        {
            lock (_lruLock)
            {
                _cache.Clear();
                _lruList.Clear();
                Statistics.Reset();
            }
        }
        
        /// <summary>
        /// Gets the memory usage of the cache in bytes (approximate).
        /// </summary>
        public long GetMemoryUsage()
        {
            long total = 0;
            foreach (var cached in _cache.Values)
            {
                // 4 bytes per int + overhead
                total += cached.MaskData.Length * 4;
            }
            return total;
        }
        
        private class CachedMask
        {
            public int[] MaskData { get; set; }
            public LinkedListNode<ROIMaskKey> ListNode { get; set; }
        }
    }
    
    /// <summary>
    /// Key for caching ROI masks.
    /// </summary>
    public struct ROIMaskKey : IEquatable<ROIMaskKey>
    {
        /// <summary>Tile index</summary>
        public readonly int Tile;
        /// <summary>Component index</summary>
        public readonly int Component;
        /// <summary>Code-block X coordinate</summary>
        public readonly int CodeBlockX;
        /// <summary>Code-block Y coordinate</summary>
        public readonly int CodeBlockY;
        /// <summary>Code-block width</summary>
        public readonly int CodeBlockWidth;
        /// <summary>Code-block height</summary>
        public readonly int CodeBlockHeight;
        /// <summary>Hash of ROI configuration</summary>
        public readonly int ConfigHash;
        
        public ROIMaskKey(int tile, int component, int cbX, int cbY, int cbWidth, int cbHeight, int configHash)
        {
            Tile = tile;
            Component = component;
            CodeBlockX = cbX;
            CodeBlockY = cbY;
            CodeBlockWidth = cbWidth;
            CodeBlockHeight = cbHeight;
            ConfigHash = configHash;
        }
        
        public bool Equals(ROIMaskKey other)
        {
            return Tile == other.Tile &&
                   Component == other.Component &&
                   CodeBlockX == other.CodeBlockX &&
                   CodeBlockY == other.CodeBlockY &&
                   CodeBlockWidth == other.CodeBlockWidth &&
                   CodeBlockHeight == other.CodeBlockHeight &&
                   ConfigHash == other.ConfigHash;
        }
        
        public override bool Equals(object? obj)
        {
            return obj is ROIMaskKey key && Equals(key);
        }
        
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + Tile;
                hash = hash * 31 + Component;
                hash = hash * 31 + CodeBlockX;
                hash = hash * 31 + CodeBlockY;
                hash = hash * 31 + CodeBlockWidth;
                hash = hash * 31 + CodeBlockHeight;
                hash = hash * 31 + ConfigHash;
                return hash;
            }
        }
        
        public override string ToString()
        {
            return $"T{Tile}C{Component}_CB({CodeBlockX},{CodeBlockY},{CodeBlockWidth}x{CodeBlockHeight})_H{ConfigHash}";
        }
    }
    
    /// <summary>
    /// Tracks cache performance statistics.
    /// </summary>
    public class CacheStatistics
    {
        private long _hits;
        private long _misses;
        private long _adds;
        private long _evictions;
        
        /// <summary>Gets the number of cache hits.</summary>
        public long Hits => _hits;
        
        /// <summary>Gets the number of cache misses.</summary>
        public long Misses => _misses;
        
        /// <summary>Gets the number of items added to cache.</summary>
        public long Adds => _adds;
        
        /// <summary>Gets the number of evictions.</summary>
        public long Evictions => _evictions;
        
        /// <summary>Gets the total number of requests.</summary>
        public long TotalRequests => _hits + _misses;
        
        /// <summary>Gets the cache hit ratio (0.0 to 1.0).</summary>
        public double HitRatio => TotalRequests > 0 ? (double)_hits / TotalRequests : 0.0;
        
        internal void RecordHit() => System.Threading.Interlocked.Increment(ref _hits);
        internal void RecordMiss() => System.Threading.Interlocked.Increment(ref _misses);
        internal void RecordAdd() => System.Threading.Interlocked.Increment(ref _adds);
        internal void RecordEviction() => System.Threading.Interlocked.Increment(ref _evictions);
        
        /// <summary>Resets all statistics to zero.</summary>
        public void Reset()
        {
            _hits = 0;
            _misses = 0;
            _adds = 0;
            _evictions = 0;
        }
        
        /// <summary>Returns a formatted string with cache statistics.</summary>
        public override string ToString()
        {
            return $"ROI Cache: Hits={Hits}, Misses={Misses}, Hit Ratio={HitRatio:P2}, " +
                   $"Adds={Adds}, Evictions={Evictions}";
        }
    }
}
