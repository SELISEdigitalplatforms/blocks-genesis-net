using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis;

public class MongoDbContextProvider : IDbContextProvider
{
    private readonly ConcurrentDictionary<(string ConnectionString, string DatabaseName), IMongoDatabase> _databases = new();
    private readonly ILogger<MongoDbContextProvider> _logger;
    private readonly ITenants _tenants;
    private readonly ActivitySource _activitySource;
    private readonly ConcurrentDictionary<string, Lazy<MongoClient>> _mongoClients = new();

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

        // Resolve the tenant snapshot on each operation so tenant-cache updates also
        // change routing. Clients and database handles are still reused below.
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

    public IMongoDatabase GetDatabase ( string connectionString, string databaseName, bool isCacheRefreshed=false )
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString), "Connection string cannot be null or empty.");

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentNullException(nameof(databaseName), "Database name cannot be null or empty.");

        // Keep isCacheRefreshed for API compatibility. Connection changes always
        // select a different entry now, regardless of the caller's refresh flag.
        return _databases.GetOrAdd((connectionString, databaseName), key =>
        {
            _logger.LogInformation("Creating database instance for: {DatabaseName}", key.DatabaseName);
            return CreateMongoClient(key.ConnectionString).GetDatabase(key.DatabaseName);
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

            return GetDatabase(dbConnection, dbName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database for tenant: {TenantId}", tenantId);
            throw new InvalidOperationException($"Could not initialize database for tenant '{tenantId}'", ex);
        }
    }

    private MongoClient CreateMongoClient(string connectionString)
    {
        // Lazy prevents concurrent cache misses from creating duplicate clients.
        return _mongoClients.GetOrAdd(connectionString, conn => new Lazy<MongoClient>(() =>
        {
            _logger.LogInformation("Creating new MongoClient for connection string.");
            var settings = MongoClientSettings.FromConnectionString(conn);
            settings.RetryReads = true;
            settings.RetryWrites = true;
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
            settings.ConnectTimeout = TimeSpan.FromSeconds(10);
            settings.ClusterConfigurator = cb => cb.Subscribe(new MongoEventSubscriber(_activitySource));
            return new MongoClient(settings);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
