using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoDbContextProvider : IDbContextProvider
    {
        private readonly ConcurrentDictionary<string, IMongoDatabase> _databases = new();
        private readonly ILogger<MongoDbContextProvider> _logger;
        private readonly ITenants _tenants;
        private readonly ActivitySource _activitySource;
        private readonly ConcurrentDictionary<string, MongoClient> _mongoClients = new();

        public MongoDbContextProvider(ILogger<MongoDbContextProvider> logger, ITenants tenants, ActivitySource activitySource)
        {
            _logger = logger;
            _tenants = tenants;
            _activitySource = activitySource;
        }

        public IMongoDatabase GetDatabase(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                throw new ArgumentNullException(nameof(tenantId), "Tenant ID cannot be null or empty.");

            // Use lazy loading for tenant databases
            return _databases.GetOrAdd(tenantId, id =>
            {
                _logger.LogInformation("Loading database for tenant: {TenantId}", id);
                return InitializeDatabaseForTenant(id);
            });
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

            var dbKey = databaseName.ToLower();

            return _databases.GetOrAdd(dbKey, _ =>
            {
                _logger.LogInformation("Creating database instance for: {DatabaseName}", dbKey);
                return CreateMongoClient(connectionString).GetDatabase(databaseName);
            });
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
                var (dbName, dbConnection) = _tenants.GetTenantDatabaseConnectionString(tenantId);
                if (string.IsNullOrWhiteSpace(dbConnection) || string.IsNullOrWhiteSpace(dbName))
                {
                    throw new KeyNotFoundException($"Database information is missing for tenant: {tenantId}");
                }

                return CreateMongoClient(dbConnection).GetDatabase(dbName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database for tenant: {tenantId}", tenantId);
                throw;
            }
        }

        private MongoClient CreateMongoClient(string connectionString)
        {
            // Reuse MongoClient instances for the same connection string
            return _mongoClients.GetOrAdd(connectionString, conn =>
            {
                _logger.LogInformation("Creating new MongoClient for connection string.");
                var settings = MongoClientSettings.FromConnectionString(conn);
                settings.ClusterConfigurator = cb => cb.Subscribe(new MongoEventSubscriber(_activitySource));
                return new MongoClient(settings);
            });
        }
    }
}
