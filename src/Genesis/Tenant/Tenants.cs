using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Blocks.Genesis
{
    public class Tenants : ITenants
    {
        private readonly ILogger<Tenants> _logger;
        private readonly IBlocksSecret _blocksSecret;
        private readonly ICacheClient _cacheClient;
        private readonly IMongoDatabase _database;
        private readonly string _tenantVersionKey = "***REMOVED***";
        private string _tenantVersion;

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
                InitializeCache();
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

        public void UpdateTenantCache()
        {
            try
            {
                var version = _cacheClient.GetStringValue(_tenantVersionKey);
                if (string.IsNullOrWhiteSpace(version) || _tenantVersion == version) return;

                _tenantVersion = version;

                // Reload the cache
                ReloadTenants();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update tenant cache.");
            }
        }

        public void UpdateTenantVersion()
        {
            try
            {
                _cacheClient.AddStringValue(_tenantVersionKey, Guid.NewGuid().ToString("n"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update tenant version.");
            }
        }

        private void InitializeCache()
        {
            _tenantVersion = _cacheClient.GetStringValue(_tenantVersionKey) ?? string.Empty;

            ReloadTenants();
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

                    // Only create collections if they are missing
                    LmtConfiguration.CreateCollectionForTrace(_blocksSecret.TraceConnectionString, tenant.TenantId);
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
                _logger.LogError(ex, $"Failed to retrieve tenant from DB for ID: {tenantId}");
                return null;
            }
        }
    }
}
