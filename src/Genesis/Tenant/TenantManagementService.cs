using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Text.Json;

namespace Blocks.Genesis
{
    /// <summary>
    /// L2 write service for tenant lifecycle operations.
    /// Only tenant-management service should use this for create/update/delete.
    /// </summary>
    public interface ITenantManagementService
    {
        Task<Tenant> CreateTenantAsync(Tenant tenant);
        Task<Tenant> UpdateTenantAsync(string tenantId, Tenant updated);
        Task DeleteTenantAsync(string tenantId);
    }

    public class TenantManagementService : ITenantManagementService
    {
        private readonly ILogger<TenantManagementService> _logger;
        private readonly IMongoDatabase _database;
        private readonly ICacheClient _cacheClient;
        private readonly ITraceCollectionEnsurer? _ensurer;
        private readonly string _tenantInvalidationChannel = "tenant:invalidate";

        public TenantManagementService(
            ILogger<TenantManagementService> logger,
            IBlocksSecret blocksSecret,
            ICacheClient cacheClient,
            ITraceCollectionEnsurer? ensurer = null)
        {
            _logger = logger;
            _cacheClient = cacheClient;
            _ensurer = ensurer;

            var settings = MongoClientSettings.FromConnectionString(blocksSecret.DatabaseConnectionString);
            settings.MaxConnectionPoolSize = 5;
            settings.MinConnectionPoolSize = 1;
            _database = new MongoClient(settings).GetDatabase(blocksSecret.RootDatabaseName);
        }

        public async Task<Tenant> CreateTenantAsync(Tenant tenant)
        {
            if (tenant == null)
                throw new ArgumentNullException(nameof(tenant));

            try
            {
                var collection = _database.GetCollection<Tenant>(BlocksConstants.TenantCollectionName);
                await collection.InsertOneAsync(tenant);

                var serialized = JsonSerializer.Serialize(tenant);
                await _cacheClient.AddStringValueAsync($"tenant:{tenant.TenantId}", serialized);

                await _cacheClient.PublishAsync(_tenantInvalidationChannel, tenant.TenantId);

                // Ensure trace collection exists and both caches are populated
                if (_ensurer != null)
                {
                    await _ensurer.EnsureAndCacheAsync(tenant.TenantId);
                }

                return tenant;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create tenant {TenantId}.", tenant.TenantId);
                throw;
            }
        }

        public async Task<Tenant> UpdateTenantAsync(string tenantId, Tenant updated)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                throw new ArgumentNullException(nameof(tenantId));
            if (updated == null)
                throw new ArgumentNullException(nameof(updated));

            try
            {
                var collection = _database.GetCollection<Tenant>(BlocksConstants.TenantCollectionName);
                var result = await collection.ReplaceOneAsync(
                    t => t.TenantId == tenantId,
                    updated,
                    new ReplaceOptions { IsUpsert = false });

                if (result.MatchedCount == 0)
                {
                    throw new KeyNotFoundException($"Tenant {tenantId} not found.");
                }

                var serialized = JsonSerializer.Serialize(updated);
                await _cacheClient.AddStringValueAsync($"tenant:{tenantId}", serialized);

                await _cacheClient.PublishAsync(_tenantInvalidationChannel, tenantId);

                return updated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update tenant {TenantId}.", tenantId);
                throw;
            }
        }

        public async Task DeleteTenantAsync(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                throw new ArgumentNullException(nameof(tenantId));

            try
            {
                var collection = _database.GetCollection<Tenant>(BlocksConstants.TenantCollectionName);
                var update = Builders<Tenant>.Update.Set(t => t.IsDisabled, true);
                var result = await collection.UpdateOneAsync(t => t.TenantId == tenantId, update);

                if (result.MatchedCount == 0)
                {
                    throw new KeyNotFoundException($"Tenant {tenantId} not found.");
                }

                await _cacheClient.RemoveKeyAsync($"tenant:{tenantId}");

                if (_ensurer != null)
                {
                    await _ensurer.RemoveEnsureAsync(tenantId);
                }

                await _cacheClient.PublishAsync(_tenantInvalidationChannel, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete tenant {TenantId}.", tenantId);
                throw;
            }
        }
    }
}
