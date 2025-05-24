using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Blocks.Genesis
{
    public class Tenants : ITenants, IDisposable
    {
        private readonly ILogger<Tenants> _logger;
        private readonly IBlocksSecret _blocksSecret;
        private readonly ICacheClient _cacheClient;
        private readonly IMongoDatabase _database;
        private readonly string _tenantVersionKey = "tenant::version";
        private readonly string _tenantUpdateChannel = "tenant::updates";
        private string _tenantVersion;
        private bool _isSubscribed = false;
        private bool _disposed = false;
        private static bool _isInitialized = false;

        // Using ConcurrentDictionary for thread-safe access and efficient lookups
        private readonly ConcurrentDictionary<string, Tenant> _tenantCache = new();

        public Tenants(ILogger<Tenants> logger, IBlocksSecret blocksSecret, ICacheClient cacheClient)
        {
            _logger = logger;
            _blocksSecret = blocksSecret;
            _cacheClient = cacheClient;

            _database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RootDatabaseName);

            try
            {
                if(!_isInitialized)
                {
                    InitializeCache();
                    // Subscribe to tenant updates
                    SubscribeToTenantUpdates().ConfigureAwait(false);
                    _isInitialized = true;
                } 
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

        public async Task UpdateTenantVersionAsync()
        {
            try
            {
                string newVersion = Guid.NewGuid().ToString("n");

                // Set the new version in Redis
                bool setSuccess = _cacheClient.AddStringValue(_tenantVersionKey, newVersion);
                if (!setSuccess)
                {
                    _logger.LogWarning("Failed to update tenant version in Redis.");
                    return;
                }

                // Update local version
                _tenantVersion = newVersion;

                // Publish the update to notify all instances
                await _cacheClient.PublishAsync(_tenantUpdateChannel, _tenantVersion);

                _logger.LogInformation("Tenant version updated to {Version} and published to channel.", newVersion);
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
                string newVersion = message.ToString();

                // Skip if we're already on this version
                if (_tenantVersion == newVersion) return;

                _logger.LogInformation("Received tenant update notification. New version: {Version}", newVersion);

                // Update local version
                _tenantVersion = newVersion;

                // Reload tenant data
                ReloadTenants();
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

                // Clear the current cache and repopulate
                _tenantCache.Clear();

                foreach (var tenant in tenants)
                {
                    _tenantCache[tenant.TenantId] = tenant;

                    if(tenant.CreatedDate > DateTime.UtcNow.AddDays(-1))
                    {
                        // Only create collections if they are missing
                        LmtConfiguration.CreateCollectionForTrace(_blocksSecret.TraceConnectionString, tenant.TenantId);
                    }
                }

                _logger.LogInformation("Reloaded {Count} tenants into cache.", tenants.Count);
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
                _logger.LogError(ex, $"Failed to retrieve tenant from DB for ID: {tenantId}");
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

            try
            {
                return _database
                    .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
                    .Find(t => t.ApplicationDomain.Equals(appName) || t.AllowedDomains.Contains(appName))
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to retrieve tenant from DB for Application name: {appName}");
                return null;
            }
        }
    }
}