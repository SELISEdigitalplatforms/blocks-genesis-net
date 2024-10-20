using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Newtonsoft.Json;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoDbContextProvider : IDbContextProvider
    {
        private readonly IDictionary<string, IMongoDatabase> _databases = new SortedDictionary<string, IMongoDatabase>();
        private readonly ILogger<MongoDbContextProvider> _logger;
        private readonly ITenants _tenants;
        private readonly ActivitySource _activitySource;

        public MongoDbContextProvider(ILogger<MongoDbContextProvider> logger, ITenants tenants, ActivitySource activitySource)
        {
            _logger = logger;
            _tenants = tenants;
            _activitySource = activitySource;

            foreach (var (tenantId, (dbName, dbConnection)) in tenants.GetTenantDatabaseConnectionStrings())
            {
                try
                {
                    if (!string.IsNullOrEmpty(dbConnection))
                    {
                        var database = CreateMongoClient(dbConnection).GetDatabase(dbName);
                        _databases.Add(tenantId, database);
                        _logger.LogInformation("Database connection established for tenant: {tenantId}, database: {dbName}", tenantId, dbName);
                    }
                    else
                    {
                        _logger.LogInformation("Tenant DB connection string missing for tenant: {tenantId}", tenantId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Unable to load tenant Data context for: {tenantId} due to an exception. Exception details: {ex}", tenantId, JsonConvert.SerializeObject(ex));
                }
            }
        }

        public IMongoDatabase GetDatabase(string tenantId)
        {
            if (_databases.TryGetValue(tenantId, out var database))
            {
                return database;
            }

            return SaveNewTenantDbConnection(tenantId);
        }

        public IMongoDatabase? GetDatabase()
        {
            var securityContext = BlocksContext.GetContext();
            return securityContext?.TenantId == null ? null : GetDatabase(securityContext.TenantId);
        }

        public IMongoDatabase GetDatabase(string connectionString, string databaseName)
        {
            var lowerCaseDbName = databaseName.ToLower();
            if (_databases.TryGetValue(lowerCaseDbName, out var database))
            {
                return database;
            }

            var newDatabase = CreateMongoClient(connectionString).GetDatabase(databaseName);
            _databases[lowerCaseDbName] = newDatabase;
            return newDatabase;
        }

        public IMongoCollection<T> GetCollection<T>(string collectionName)
        {
            var database = GetDatabase();
            if (database == null)
            {
                throw new InvalidOperationException("Database context is not available. Ensure that the tenant ID is set correctly.");
            }
            return database.GetCollection<T>(collectionName);
        }

        public IMongoCollection<T> GetCollection<T>(string tenantId, string collectionName)
        {
            var database = GetDatabase(tenantId);
            return database.GetCollection<T>(collectionName);
        }

        private MongoClient CreateMongoClient(string connectionString)
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ClusterConfigurator = cb =>
            {
                cb.Subscribe(new MongoEventSubscriber(_activitySource));
            };

            return new MongoClient(settings);
        }

        private IMongoDatabase SaveNewTenantDbConnection(string tenantId)
        {
            try
            {
                var (dbName, dbConnection) = _tenants.GetTenantDatabaseConnectionString(tenantId);

                if (string.IsNullOrWhiteSpace(dbConnection))
                {
                    throw new KeyNotFoundException($"Database Connection string is not found for tenant: {tenantId}");
                }

                var database = CreateMongoClient(dbConnection).GetDatabase(dbName);
                _databases[tenantId] = database;
                _logger.LogInformation("New database connection saved for tenant: {tenantId}, database: {dbName}", tenantId, dbName);

                return database;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error while saving new tenant DB connection for tenant: {tenantId}", tenantId);
                throw;
            }
        }
    }
}
