using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Blocks.Genesis
{
    public class MongoDbContextProvider : IDbContextProvider, IDisposable
    {
        private readonly ILogger<MongoDbContextProvider> _logger;
        private readonly ITenants _tenants;
        private readonly ActivitySource _activitySource;
        private readonly IMemoryCache _mongoClientCache;
        private bool _disposed = false;

        public MongoDbContextProvider(
            ILogger<MongoDbContextProvider> logger, 
            ITenants tenants, 
            ActivitySource activitySource,
            IMemoryCache memoryCache)
        {
            _logger = logger;
            _tenants = tenants;
            _activitySource = activitySource;
            _mongoClientCache = memoryCache;
        }

        // Get or create MongoClient with bounded L1 cache
        private MongoClient GetOrCreateMongoClient(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentNullException(nameof(connectionString), "Connection string cannot be null or empty.");

            // Create cache key from connection string hash (handles uniqueness)
            var cacheKey = BuildMongoClientCacheKey(connectionString);

            return _mongoClientCache.GetOrCreate(cacheKey, entry =>
            {
                // Size: 1 = each client counts as 1 unit
                entry.Size = 1;
                
                // TTL: 30 min absolute, 10 min sliding
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
                entry.SlidingExpiration = TimeSpan.FromMinutes(10);

                // When this client is evicted, dispose it properly
                entry.RegisterPostEvictionCallback((k, v, reason, state) =>
                {
                    if (v is MongoClient client)
                    {
                        _logger.LogInformation("Disposing evicted MongoClient. Key: {Key}, Reason: {Reason}", k, reason);
                        try
                        {
                            client.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error disposing evicted MongoClient.");
                        }
                    }
                });

                // Create settings with bounded connection pool
                var settings = MongoClientSettings.FromConnectionString(connectionString);
                settings.MaxConnectionPoolSize = 5;     // Limit to 5 per client (not 100)
                settings.MinConnectionPoolSize = 1;
                settings.ClusterConfigurator = cb => cb.Subscribe(new MongoEventSubscriber(_activitySource));

                _logger.LogInformation("Creating new MongoClient. Cache key: {Key}", cacheKey);
                return new MongoClient(settings);
            });
        }

        private static string BuildMongoClientCacheKey(string connectionString)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(connectionString));
            return $"mongo:client:{Convert.ToHexString(hash)}";
        }

        public IMongoDatabase GetDatabase(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                throw new ArgumentNullException(nameof(tenantId), "Tenant ID cannot be null or empty.");

            _logger.LogInformation("Getting database for tenant: {TenantId}", tenantId);
            return InitializeDatabaseForTenant(tenantId);
        }

        public IMongoDatabase? GetDatabase()
        {
            var securityContext = BlocksContext.GetContext();
            if (securityContext?.TenantId == null)
            {
                _logger.LogWarning("Tenant ID is missing in the security context.");
                return null;
            }

            return GetDatabase(securityContext.TenantId);
        }

        public IMongoDatabase GetDatabase(string connectionString, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentNullException(nameof(connectionString), "Connection string cannot be null or empty.");
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentNullException(nameof(databaseName), "Database name cannot be null or empty.");

            _logger.LogInformation("Getting database: {DatabaseName}", databaseName);
            
            // Get cached MongoClient
            var client = GetOrCreateMongoClient(connectionString);
            
            // Derive IMongoDatabase on-demand (don't cache separately)
            return client.GetDatabase(databaseName);
        }

        public IMongoCollection<T> GetCollection<T>(string collectionName)
        {
            var database = GetDatabase();
            if (database == null)
            {
                throw new InvalidOperationException("Database context is not available. Ensure the tenant ID is set correctly.");
            }

            return database.GetCollection<T>(collectionName);
        }

        public IMongoCollection<T> GetCollection<T>(string tenantId, string collectionName)
        {
            var database = GetDatabase(tenantId);
            return database.GetCollection<T>(collectionName);
        }

        private IMongoDatabase InitializeDatabaseForTenant(string tenantId)
        {
            try
            {
                var tenant = _tenants.GetTenantByID(tenantId);
                var dbName = tenant?.DBName;
                var dbConnection = tenant?.DbConnectionString;
                if (string.IsNullOrWhiteSpace(dbConnection) || string.IsNullOrWhiteSpace(dbName))
                {
                    throw new KeyNotFoundException($"Database information is missing for tenant: {tenantId}");
                }

                return GetDatabase(dbConnection, dbName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database for tenant: {TenantId}", tenantId);
                throw new InvalidOperationException($"Could not initialize database for tenant '{tenantId}'", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
        }
    }
}
