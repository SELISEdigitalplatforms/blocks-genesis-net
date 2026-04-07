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
        // MongoClient is expensive to create (TCP connection pool setup).
        // Aggressive sliding (5 min): inactive tenants release their connection pool quickly.
        // Absolute (20 min): active tenants are never rebuilt mid-load, even under sustained traffic.
        private static readonly TimeSpan MongoClientAbsoluteExpiration = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan MongoClientSlidingExpiration = TimeSpan.FromMinutes(5);

        // Keep MongoClient cache intentionally small: each client holds a TCP connection pool.
        // 100 clients × 5 connections = 500 open connections to MongoDB per pod — already significant.
        // Inactive tenants are evicted by sliding TTL; hot tenants stay warm.
        private static readonly MemoryCacheOptions MongoClientCacheOptions = new()
        {
            SizeLimit = 100,
            CompactionPercentage = 0.25,
            ExpirationScanFrequency = TimeSpan.FromMinutes(5)
        };

        private readonly ILogger<MongoDbContextProvider> _logger;
        private readonly ITenants _tenants;
        private readonly ActivitySource _activitySource;
        private readonly IMemoryCache _mongoClientCache;
        private bool _disposed = false;

        public MongoDbContextProvider(
            ILogger<MongoDbContextProvider> logger,
            ITenants tenants,
            ActivitySource activitySource)
        {
            _logger = logger;
            _tenants = tenants;
            _activitySource = activitySource;
            _mongoClientCache = new MemoryCache(MongoClientCacheOptions);
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
                entry.Size = 1;
                entry.AbsoluteExpirationRelativeToNow = MongoClientAbsoluteExpiration;
                entry.SlidingExpiration = MongoClientSlidingExpiration;

                // When this client is evicted, dispose it properly
                entry.RegisterPostEvictionCallback((k, v, reason, state) =>
                {
                    if (v is MongoClient client)
                    {
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
