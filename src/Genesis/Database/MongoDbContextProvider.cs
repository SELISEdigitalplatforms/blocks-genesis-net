using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Newtonsoft.Json;

namespace Blocks.Genesis
{
    internal class MongoDbContextProvider : IDbContextProvider
    {
        private readonly IDictionary<string, IMongoDatabase> _databases = new SortedDictionary<string, IMongoDatabase>();
        private readonly ILogger<MongoDbContextProvider> _logger;
        private readonly ITenants _tenants;
        private readonly ISecurityContext _securityContext;

        public MongoDbContextProvider(ILogger<MongoDbContextProvider> logger, ITenants tenants, ISecurityContext securityContext)
        {
            _logger = logger;
            _tenants = tenants;
            _securityContext = securityContext;

            foreach (var (tenantId, tenantDbConnection) in tenants.GetTenantDatabaseConnectionStrings())
            {
                try
                {
                    if (!string.IsNullOrEmpty(tenantDbConnection))
                    {
                        var database = new MongoClient(tenantDbConnection).GetDatabase(tenantId);
                        _databases.Add(tenantId.ToLower(), database);
                    }
                    else
                    {
                        _logger.LogInformation("Tenant DB connection string missing for tenant : {tenant}", tenantId);
                    }

                }
                catch (Exception ex)
                {
                    _logger.LogInformation("Unable to load tenant Data context for: {tenant} due to an exception. Exception details :{ex}", tenantId, JsonConvert.SerializeObject(ex));
                }
            }
        }

        public IMongoDatabase GetDatabase(string databaseName)
        {
            var databaseExists = _databases.ContainsKey(databaseName.ToLower());
            if (databaseExists)
            {
                return _databases[databaseName.ToLower()];
            }

            return SaveNewTenantDbConnection(databaseName);
        }

        public IMongoDatabase GetDatabase()
        {
            return GetDatabase(_securityContext.TenantId.ToLower());
        }

        public IMongoDatabase GetDatabase(string connectionString, string databaseName)
        {
            var databaseExists = _databases.ContainsKey(databaseName.ToLower());

            if (databaseExists)
            {
                return _databases[databaseName.ToLower()];
            }

            var database = new MongoClient(connectionString).GetDatabase(databaseName);

            _databases.Add(databaseName.ToLower(), database);

            return database;
        }

        public IMongoCollection<T> GetCollection<T>(string collectionName)
        {
            var database = GetDatabase();
            return database.GetCollection<T>(collectionName);
        }

        public IMongoCollection<T> GetCollection<T>(string databaseName, string collectionName)
        {
            var database = GetDatabase(databaseName);
            return database.GetCollection<T>(collectionName);
        }

        private IMongoDatabase SaveNewTenantDbConnection(string databaseName)
        {
            var tenantDbConnection = _tenants.GetTenantDatabaseConnectionString(databaseName);
            var database = new MongoClient(tenantDbConnection).GetDatabase(databaseName);
            _databases.Add(databaseName.ToLower(), database);

            return database;
        }
    }
}
