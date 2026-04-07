using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Blocks.Genesis
{
    public class Tenants : ITenantLookup, IDisposable
    {
        // Tenant data is invalidated in real-time via Redis pub/sub.
        // TTLs are a safety net only — not the primary freshness mechanism.
        // Aggressive sliding (5 min): a tenant not seen in 5 min frees the L1 slot back to Redis.
        // Absolute (10 min): caps worst-case stale data if pub/sub misses an event.
        private static readonly TimeSpan TenantAbsoluteExpiration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan TenantSlidingExpiration = TimeSpan.FromMinutes(5);

        private readonly ILogger<Tenants> _logger;
        private readonly ICacheClient _cacheClient;
        private readonly IMemoryCache _memoryCache;
        private readonly string _rootConnectionString;
        private readonly string _rootDatabaseName;
        private readonly MongoClient? _rootMongoClient;
        private readonly string _tenantInvalidationChannel = "tenant:invalidate";

        private volatile bool _isSubscribed = false;
        private volatile bool _disposed = false;

        public Tenants(ILogger<Tenants> logger, IBlocksSecret blocksSecret, ICacheClient cacheClient, IMemoryCache memoryCache)
        {
            _logger = logger;
            _cacheClient = cacheClient;
            _memoryCache = memoryCache;
            _rootConnectionString = blocksSecret.DatabaseConnectionString;
            _rootDatabaseName = blocksSecret.RootDatabaseName;

            var settings = MongoClientSettings.FromConnectionString(_rootConnectionString);
            settings.MaxConnectionPoolSize = 5;
            settings.MinConnectionPoolSize = 1;
            _rootMongoClient = new MongoClient(settings);

            try
            {
                // Subscribe to tenant invalidation notifications from other pods
                SubscribeToTenantUpdates();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to tenant invalidation channel.");
            }
        }

        // L1 is intentionally small — a pod serves only a fraction of the 4k+ tenant fleet.
        // L2 (Redis) handles misses at ~1-2ms; L3 (MongoDB) is last resort.
        public Tenant? GetTenantByID(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return null;

            var cacheKey = GetTenantCacheKey(tenantId);
            
            // Check if already in L1
            if (_memoryCache.TryGetValue(cacheKey, out Tenant? cached))
                return cached;
            
            // L2: Try Redis
            var cachedJson = _cacheClient.GetStringValue($"tenant:{tenantId}");
            if (!string.IsNullOrEmpty(cachedJson))
            {
                var tenant = System.Text.Json.JsonSerializer.Deserialize<Tenant>(cachedJson);
                if (tenant != null)
                {
                    _memoryCache.Set(cacheKey, tenant, CreateTenantCacheEntryOptions());
                    return tenant;
                }
            }
            
            // L3: Try MongoDB
            var tenantFromDb = GetTenantFromDb(tenantId);
            if (tenantFromDb != null)
            {
                _memoryCache.Set(cacheKey, tenantFromDb, CreateTenantCacheEntryOptions());
            }
            
            return tenantFromDb;
        }

        Tenant? ITenantLookup.GetTenantByApplicationDomain(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return null;

            appName = NormalizeTenantDomain(appName);

            var cacheKey = GetTenantDomainCacheKey(appName);

            // L1: return cached tenant directly
            if (_memoryCache.TryGetValue(cacheKey, out Tenant? cached))
                return cached;

            try
            {
                var builder = Builders<Tenant>.Filter;

                var domainMatch = builder.Or(
                    builder.Eq(t => t.ApplicationDomain, appName),
                    builder.AnyEq(t => t.AllowedDomains, appName));

                var tenant = GetRootDatabase()
                    .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
                    .Find(domainMatch)
                    .FirstOrDefault();

                if (tenant != null)
                {
                    _memoryCache.Set(cacheKey, tenant, CreateTenantCacheEntryOptions());
                    TrackTenantDomainCacheKey(tenant.TenantId, cacheKey);
                }

                return tenant;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve tenant from DB for Application name: {AppName}", appName);
                return null;
            }
        }

        // Subscribe to invalidation events published by tenant-management service
        private void SubscribeToTenantUpdates()
        {
            if (_isSubscribed) return;

            try
            {
                _ = _cacheClient.SubscribeAsync(_tenantInvalidationChannel, HandleTenantInvalidation);
                _isSubscribed = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to tenant invalidation channel.");
            }
        }

        // Refresh only the tenant referenced by the update message, and only if it exists in L1 cache.
        private void HandleTenantInvalidation(RedisChannel channel, RedisValue message)
        {
            try
            {
                var tenantId = message.ToString();
                if (string.IsNullOrEmpty(tenantId)) return;

                var tenantCacheKey = GetTenantCacheKey(tenantId);
                var hasTenantCache = _memoryCache.TryGetValue(tenantCacheKey, out _);
                var trackedDomainKeys = GetTrackedTenantDomainCacheKeys(tenantId);

                if (!hasTenantCache && trackedDomainKeys.Count == 0)
                {
                    return;
                }

                var tenant = GetTenantFromDb(tenantId);
                if (tenant == null)
                {
                    _memoryCache.Remove(tenantCacheKey);
                    RemoveTenantDomainEntries(tenantId, trackedDomainKeys);
                    return;
                }

                if (hasTenantCache)
                {
                    _memoryCache.Set(tenantCacheKey, tenant, CreateTenantCacheEntryOptions());
                }

                RefreshTrackedTenantDomainEntries(tenantId, trackedDomainKeys, tenant);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling tenant invalidation.");
            }
        }

        private static string GetTenantCacheKey(string tenantId) => $"tenant:{tenantId}";

        private static string GetTenantDomainCacheKey(string appName) => $"tenant:domain:{NormalizeTenantDomain(appName)}";

        private static string GetTenantDomainIndexKey(string tenantId) => $"tenant:domains:{tenantId}";

        private static string NormalizeTenantDomain(string appName)
        {
            return appName.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? appName
                : $"https://{appName}";
        }

        private static MemoryCacheEntryOptions CreateTenantCacheEntryOptions()
        {
            return new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = TenantAbsoluteExpiration,
                SlidingExpiration = TenantSlidingExpiration
            };
        }

        private void TrackTenantDomainCacheKey(string tenantId, string domainCacheKey)
        {
            var indexKey = GetTenantDomainIndexKey(tenantId);
            var trackedKeys = GetTrackedTenantDomainCacheKeys(tenantId);
            trackedKeys.Add(domainCacheKey);
            _memoryCache.Set(indexKey, trackedKeys, CreateTenantCacheEntryOptions());
        }

        private HashSet<string> GetTrackedTenantDomainCacheKeys(string tenantId)
        {
            var indexKey = GetTenantDomainIndexKey(tenantId);
            if (_memoryCache.TryGetValue(indexKey, out HashSet<string>? trackedKeys) && trackedKeys != null)
            {
                return trackedKeys;
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private void RemoveTenantDomainEntries(string tenantId, IEnumerable<string> trackedDomainKeys)
        {
            foreach (var trackedDomainKey in trackedDomainKeys)
            {
                _memoryCache.Remove(trackedDomainKey);
            }

            _memoryCache.Remove(GetTenantDomainIndexKey(tenantId));
        }

        private void RefreshTrackedTenantDomainEntries(string tenantId, HashSet<string> trackedDomainKeys, Tenant tenant)
        {
            if (trackedDomainKeys.Count == 0)
            {
                return;
            }

            var currentDomainKeys = GetCurrentTenantDomainCacheKeys(tenant);
            var remainingTrackedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var trackedDomainKey in trackedDomainKeys)
            {
                if (currentDomainKeys.Contains(trackedDomainKey))
                {
                    _memoryCache.Set(trackedDomainKey, tenant, CreateTenantCacheEntryOptions());
                    remainingTrackedKeys.Add(trackedDomainKey);
                    continue;
                }

                _memoryCache.Remove(trackedDomainKey);
            }

            if (remainingTrackedKeys.Count == 0)
            {
                _memoryCache.Remove(GetTenantDomainIndexKey(tenantId));
                return;
            }

            _memoryCache.Set(GetTenantDomainIndexKey(tenantId), remainingTrackedKeys, CreateTenantCacheEntryOptions());
        }

        private static HashSet<string> GetCurrentTenantDomainCacheKeys(Tenant tenant)
        {
            var cacheKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(tenant.ApplicationDomain))
            {
                cacheKeys.Add(GetTenantDomainCacheKey(tenant.ApplicationDomain));
            }

            if (tenant.AllowedDomains == null)
            {
                return cacheKeys;
            }

            foreach (var allowedDomain in tenant.AllowedDomains)
            {
                if (!string.IsNullOrWhiteSpace(allowedDomain))
                {
                    cacheKeys.Add(GetTenantDomainCacheKey(allowedDomain));
                }
            }

            return cacheKeys;
        }

        private IMongoDatabase GetRootDatabase()
        {
            if (_rootMongoClient != null)
            {
                return _rootMongoClient.GetDatabase(_rootDatabaseName);
            }

            var settings = MongoClientSettings.FromConnectionString(_rootConnectionString);
            settings.MaxConnectionPoolSize = 5;
            settings.MinConnectionPoolSize = 1;
            return new MongoClient(settings).GetDatabase(_rootDatabaseName);
        }

        private Tenant? GetTenantFromDb(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return null;
            if (_disposed) return null;

            try
            {
                return GetRootDatabase()
                    .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
                    .Find(t => t.ItemId == tenantId || t.TenantId == tenantId)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve tenant from DB for ID: {TenantId}", tenantId);
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            if (_isSubscribed)
            {
                try
                {
                    var unsubTask = _cacheClient.UnsubscribeAsync(_tenantInvalidationChannel);
                    if (!unsubTask.Wait(TimeSpan.FromSeconds(5)))
                    {
                        _logger.LogWarning("Timeout waiting for tenant invalidation channel unsubscribe.");
                    }
                    _isSubscribed = false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error unsubscribing from tenant invalidation channel.");
                }
            }

            try
            {
                _rootMongoClient?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing root MongoClient.");
            }
        }
    }
}