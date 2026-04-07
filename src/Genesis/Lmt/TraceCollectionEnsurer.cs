using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections.Concurrent;

namespace Blocks.Genesis
{
    /// <summary>
    /// Central coordinator for trace collection ensure + cache synchronization.
    /// Handles: create collection + update L1 memory cache + update L2 Redis cache.
    /// Used by exporter and tenant management lifecycle.
    /// </summary>
    public interface ITraceCollectionEnsurer
    {
        Task EnsureAndCacheAsync(string tenantId);
        Task RemoveEnsureAsync(string tenantId);
    }

    public sealed class TraceCollectionEnsurer : ITraceCollectionEnsurer
    {
        private readonly IBlocksSecret _blocksSecret;
        private readonly ICacheClient _cacheClient;
        private readonly IMemoryCache _memoryCache;
        private readonly IMongoDatabase? _traceDatabase;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> TenantEnsureLocks = new();

        private const string LocalEnsurePrefix = "trace:ensure:l1:";
        private const string RedisEnsurePrefix = "trace:ensure:l2:";

        // Trace collections are permanent once created — 60 min L1 TTL avoids redundant DB checks
        // while still recovering from pod restarts within a reasonable window.
        private static readonly TimeSpan LocalEnsureTtl = TimeSpan.FromMinutes(60);

        public TraceCollectionEnsurer(
            IBlocksSecret blocksSecret,
            ICacheClient cacheClient,
            IMemoryCache memoryCache)
        {
            _blocksSecret = blocksSecret;
            _cacheClient = cacheClient;
            _memoryCache = memoryCache;

            if (!string.IsNullOrWhiteSpace(_blocksSecret.TraceConnectionString))
            {
                _traceDatabase = LmtConfiguration.GetMongoDatabase(_blocksSecret.TraceConnectionString, LmtConfiguration.TraceDatabaseName);
            }
        }

        public async Task EnsureAndCacheAsync(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return;
            }

            // Check if already in L1
            if (_memoryCache.TryGetValue(GetLocalEnsureKey(tenantId), out _))
            {
                return;
            }

            // Check if already in L2
            if (await _cacheClient.KeyExistsAsync(GetRedisEnsureKey(tenantId)))
            {
                MarkLocalEnsured(tenantId);
                return;
            }

            var tenantLock = TenantEnsureLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
            await tenantLock.WaitAsync();
            try
            {
                // Double-check after lock acquisition.
                if (_memoryCache.TryGetValue(GetLocalEnsureKey(tenantId), out _))
                {
                    return;
                }

                if (await _cacheClient.KeyExistsAsync(GetRedisEnsureKey(tenantId)))
                {
                    MarkLocalEnsured(tenantId);
                    return;
                }

                // Check if collection exists in DB
                var exists = false;
                if (_traceDatabase != null)
                {
                    exists = await TraceCollectionExistsAsync(tenantId);
                }

                // If not in DB, create it
                if (!exists)
                {
                    var traceConnection = _blocksSecret.TraceConnectionString;
                    if (!string.IsNullOrWhiteSpace(traceConnection))
                    {
                        LmtConfiguration.CreateCollectionForTrace(traceConnection, tenantId);
                    }
                }

                // Verify existence before marking caches.
                if (_traceDatabase != null && !await TraceCollectionExistsAsync(tenantId))
                {
                    throw new InvalidOperationException($"Failed to ensure trace collection for tenant '{tenantId}'.");
                }

                // Update both caches
                MarkLocalEnsured(tenantId);
                await MarkRedisEnsuredAsync(tenantId);
            }
            finally
            {
                tenantLock.Release();

                // Avoid unbounded lock dictionary growth for one-time tenant IDs.
                if (tenantLock.CurrentCount == 1)
                {
                    TenantEnsureLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(tenantId, tenantLock));
                }
            }
        }

        public async Task RemoveEnsureAsync(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return;
            }

            _memoryCache.Remove(GetLocalEnsureKey(tenantId));
            await _cacheClient.RemoveKeyAsync(GetRedisEnsureKey(tenantId));
        }

        private async Task<bool> TraceCollectionExistsAsync(string collectionName)
        {
            if (_traceDatabase == null)
            {
                return false;
            }

            var filter = new BsonDocument("name", collectionName);
            var options = new ListCollectionNamesOptions { Filter = filter };
            using var cursor = await _traceDatabase.ListCollectionNamesAsync(options);
            return await cursor.AnyAsync();
        }

        private void MarkLocalEnsured(string tenantId)
        {
            _memoryCache.Set(
                GetLocalEnsureKey(tenantId),
                true,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = LocalEnsureTtl,
                    Size = 1
                });
        }

        private async Task MarkRedisEnsuredAsync(string tenantId)
        {
            await _cacheClient.AddStringValueAsync(
                GetRedisEnsureKey(tenantId),
                "1");
        }

        private static string GetLocalEnsureKey(string tenantId) => $"{LocalEnsurePrefix}{tenantId}";
        private static string GetRedisEnsureKey(string tenantId) => $"{RedisEnsurePrefix}{tenantId}";
    }
}
