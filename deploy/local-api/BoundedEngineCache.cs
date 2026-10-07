// Infrastructure replacement for the pinned engine's unbounded dictionary and
// obsolete BinaryFormatter persistence. Astronomy and rule code are unchanged.
using System.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;

namespace VedAstro.Library;

public static class CacheManager
{
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 32000 });
    public static int CacheUseCount;
    public static int CacheNotUseCount;

    public static T GetCache<T>(CacheKey key, Func<T> heavyComputation)
    {
        if (Cache.TryGetValue(key, out T? value))
        {
            Interlocked.Increment(ref CacheUseCount);
            return value!;
        }
        Interlocked.Increment(ref CacheNotUseCount);
        var result = heavyComputation();
        Cache.Set(key, result, new MemoryCacheEntryOptions
        {
            Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
            SlidingExpiration = TimeSpan.FromMinutes(10),
        });
        return result;
    }

    // This service stores no persistent birth data or serialized object cache.
    public static void SaveCacheToDisk() { }
    public static void LoadCacheFromDisk() { }
    public static void LoadCacheFromDisk0() { }
    public static IEnumerable GetKeys(this IMemoryCache memoryCache) =>
        memoryCache is MemoryCache cache ? cache.Keys : Array.Empty<object>();
    public static IEnumerable<T> GetKeys<T>(this IMemoryCache memoryCache) =>
        GetKeys(memoryCache).Cast<object>().OfType<T>();
}
