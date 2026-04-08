using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class Tenants : ITenants, IDisposable
    {
        public const string TenantCacheUpdateActionUpsert = "upsert";
        public const string TenantCacheUpdateActionRemove = "remove";

        private readonly ILogger<Tenants> _logger;
        private readonly IBlocksSecret _blocksSecret;
        private readonly ICacheClient _cacheClient;
        private readonly IMongoDatabase _database;
        private readonly string _tenantUpdateChannel = "tenant::updates";
        private bool _isSubscribed = false;
        private bool _disposed = false;

        private readonly ConcurrentDictionary<string, Tenant> _tenantCache = [];

        public Tenants(ILogger<Tenants> logger, IBlocksSecret blocksSecret, ICacheClient cacheClient)
        {
            _logger = logger;
            _blocksSecret = blocksSecret;
            _cacheClient = cacheClient;

            _database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RootDatabaseName);

            try
            {
                InitializeCache();
                // Subscribe to tenant updates
                SubscribeToTenantUpdates().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize tenant cache.");
            }
        }

        public Tenant? GetTenantByID(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return null;

            // Try to get tenant from the in-memory cache
            if (_tenantCache.TryGetValue(tenantId, out var tenant))
                return tenant;


            // If not found in cache, fetch from database and update cache
            tenant = GetTenantFromDb(tenantId);
            if (tenant != null)
            {
                _tenantCache[tenant.TenantId] = tenant;
            }

            return tenant;
        }

        public Dictionary<string, (string, string)> GetTenantDatabaseConnectionStrings()
        {
            return _tenantCache.ToDictionary(
                kvp => kvp.Key,
                kvp => (kvp.Value.DBName, kvp.Value.DbConnectionString));
        }

        public (string?, string?) GetTenantDatabaseConnectionString(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return (null, null);

            var tenant = GetTenantByID(tenantId);
            return tenant is null ? (null, null) : (tenant.DBName, tenant.DbConnectionString);
        }

        public JwtTokenParameters? GetTenantTokenValidationParameter(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return null;

            var tenant = GetTenantByID(tenantId);
            return tenant?.JwtTokenParameters;
        }

        public async Task UpdateTenantVersionAsync(TenantCacheUpdateMessage cacheUpdate)
        {
            if (cacheUpdate is null)
            {
                throw new ArgumentNullException(nameof(cacheUpdate));
            }

            try
            {
                var normalizedUpdate = NormalizeCacheUpdate(cacheUpdate);
                if (normalizedUpdate is null)
                {
                    _logger.LogWarning("Skipping invalid tenant cache update payload.");
                    return;
                }

                // Publish the update to notify all instances
                await _cacheClient.PublishAsync(_tenantUpdateChannel, JsonSerializer.Serialize(normalizedUpdate));

                _logger.LogInformation(
                    "Tenant cache update published. TenantId: {TenantId}, Action: {Action}",
                    ResolveTenantId(normalizedUpdate),
                    normalizedUpdate.Action);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update tenant version.");
            }
        }

        private void InitializeCache()
        {
            ReloadTenants();
        }

        private async Task SubscribeToTenantUpdates()
        {
            if (_isSubscribed) return;

            try
            {
                await _cacheClient.SubscribeAsync(_tenantUpdateChannel, HandleTenantUpdate);
                _isSubscribed = true;
                _logger.LogInformation("Successfully subscribed to tenant updates channel.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to tenant updates channel.");
            }
        }

        private void HandleTenantUpdate(RedisChannel channel, RedisValue message)
        {
            try
            {
                var cacheUpdate = ParseTenantCacheUpdate(message.ToString());
                if (cacheUpdate is null)
                {
                    _logger.LogWarning("Received invalid tenant update payload.");
                    return;
                }

                cacheUpdate = NormalizeCacheUpdate(cacheUpdate);
                if (cacheUpdate is null)
                {
                    _logger.LogWarning("Skipping tenant update due to missing action/tenant details.");
                    return;
                }

                var tenantId = ResolveTenantId(cacheUpdate);

                _logger.LogInformation(
                    "Received tenant update notification. TenantId: {TenantId}, Action: {Action}",
                    tenantId,
                    cacheUpdate.Action);

                if (cacheUpdate.Action == TenantCacheUpdateActionRemove)
                {
                    if (!string.IsNullOrWhiteSpace(tenantId))
                    {
                        _tenantCache.TryRemove(tenantId, out _);
                    }

                    return;
                }

                var tenant = cacheUpdate.Tenant;
                if (tenant is null)
                {
                    return;
                }

                if (tenant.IsDisabled)
                {
                    _tenantCache.TryRemove(tenant.TenantId, out _);
                    return;
                }

                bool isNewTenant = !_tenantCache.ContainsKey(tenant.TenantId);
                _tenantCache[tenant.TenantId] = tenant;

                if (isNewTenant)
                {
                    EnsureTraceCollectionExists(tenant);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling tenant update notification.");
            }
        }

        private void ReloadTenants()
        {
            try
            {
                var tenants = _database
                    .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
                    .Find(FilterDefinition<Tenant>.Empty)
                    .ToList();

                _tenantCache.Clear();
                foreach (var tenant in tenants ?? [])
                {
                    if (!tenant.IsDisabled)
                    {
                        _tenantCache[tenant.TenantId] = tenant;
                        if (tenant.CreatedDate > DateTime.UtcNow.AddDays(-1))
                        {
                            EnsureTraceCollectionExists(tenant);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload tenants into cache.");
            }
        }


        private Tenant? GetTenantFromDb(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return null;

            try
            {
                return _database
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

            // Unsubscribe from tenant updates
            if (_isSubscribed)
            {
                try
                {
                    _cacheClient.UnsubscribeAsync(_tenantUpdateChannel).Wait();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error unsubscribing from tenant updates channel.");
                }
            }

            _disposed = true;
        }

        public Tenant? GetTenantByApplicationDomain(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return null;

            appName = appName.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? appName : $"https://{appName}";

            var cachedTenant = _tenantCache.Values.FirstOrDefault(tenant =>
                string.Equals(tenant.ApplicationDomain, appName, StringComparison.OrdinalIgnoreCase)
                || tenant.AllowedDomains.Any(domain => string.Equals(domain, appName, StringComparison.OrdinalIgnoreCase)));

            if (cachedTenant != null)
            {
                return cachedTenant;
            }

            try
            {
                var builder = Builders<Tenant>.Filter;

                var domainMatch = builder.Or(
                    builder.Eq(t => t.ApplicationDomain, appName),
                    builder.AnyEq(t => t.AllowedDomains, appName));

                var tenant = _database
                    .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
                    .Find(domainMatch)
                    .FirstOrDefault();

                if (tenant != null)
                {
                    _tenantCache[tenant.TenantId] = tenant;
                }

                return tenant;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve tenant from DB for Application name: {AppName}", appName);
                return null;
            }
        }


        private void EnsureTraceCollectionExists(Tenant tenant)
        {
            LmtConfiguration.CreateCollectionForTrace(
                _blocksSecret.TraceConnectionString,
                tenant.TenantId);
        }

        private static TenantCacheUpdateMessage? ParseTenantCacheUpdate(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                try
                {
                    var cacheUpdate = JsonSerializer.Deserialize<TenantCacheUpdateMessage>(message);
                    if (cacheUpdate != null)
                    {
                        return cacheUpdate;
                    }
                }
                catch (JsonException)
                {
                }
            }

            return null;
        }

        private static TenantCacheUpdateMessage? NormalizeCacheUpdate(TenantCacheUpdateMessage cacheUpdate)
        {
            var action = (cacheUpdate.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (action != TenantCacheUpdateActionRemove && action != TenantCacheUpdateActionUpsert)
            {
                return null;
            }

            if (action == TenantCacheUpdateActionRemove)
            {
                var tenantId = ResolveTenantId(cacheUpdate);
                return string.IsNullOrWhiteSpace(tenantId)
                    ? null
                    : cacheUpdate with { Action = TenantCacheUpdateActionRemove, TenantId = tenantId };
            }

            if (cacheUpdate.Tenant is null || string.IsNullOrWhiteSpace(cacheUpdate.Tenant.TenantId))
            {
                return null;
            }

            return cacheUpdate with { Action = TenantCacheUpdateActionUpsert, TenantId = cacheUpdate.Tenant.TenantId };
        }

        private static string? ResolveTenantId(TenantCacheUpdateMessage cacheUpdate)
        {
            return string.IsNullOrWhiteSpace(cacheUpdate.TenantId)
                ? cacheUpdate.Tenant?.TenantId
                : cacheUpdate.TenantId;
        }
    }
}