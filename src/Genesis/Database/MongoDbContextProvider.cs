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
                        var database = new MongoClient(dbConnection).GetDatabase(dbName);
                        _databases.Add(tenantId, database);
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

        public IMongoDatabase GetDatabase(string tenantId)
        {
            var databaseExists = _databases.ContainsKey(tenantId);

            if (databaseExists)
            {
                return _databases[tenantId];
            }

            return SaveNewTenantDbConnection(tenantId);
        }

        public IMongoDatabase? GetDatabase()
        {
            var securityContext = BlocksContext.GetContext();
            return securityContext == null || securityContext.TenantId == null ? null : GetDatabase(securityContext.TenantId);
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

        public IMongoCollection<T> GetCollection<T>(string tenantId, string collectionName)
        {
            var database = GetDatabase(tenantId);
            return database.GetCollection<T>(collectionName);
        }

        public T RunMongoCommandWithActivity<T>(string collectionName, string action, Func<T> mongoCommand)
        {
            var currentActivity = Activity.Current;
            var securityContext = BlocksContext.GetContext();

            using var activity = _activitySource.StartActivity($"MongoDb::{action}", ActivityKind.Producer, currentActivity?.Context ?? default);

            activity?.AddTag("collectionName", collectionName);
            activity?.AddTag("operationType", action);
            activity?.SetCustomProperty("TenantId", securityContext?.TenantId);

            try
            {
                var result = mongoCommand();
                activity?.AddTag("Status", "Success");
                return result;
            }
            catch (Exception ex)
            {
                activity?.AddTag("Status", "Failure");
                activity?.AddTag("ExceptionMessage", ex.Message);
                throw;
            }
            finally
            {
                activity?.Stop();
            }
        }

        // Asynchronous method
        public async Task<T> RunMongoCommandWithActivityAsync<T>(string collectionName, string action, Func<Task<T>> mongoCommand)
        {
            var currentActivity = Activity.Current;
            var securityContext = BlocksContext.GetContext();

            using var activity = _activitySource.StartActivity($"MongoDb::{action}", ActivityKind.Producer, currentActivity?.Context ?? default);

            activity?.AddTag("collectionName", collectionName);
            activity?.AddTag("operationType", action);
            activity?.SetCustomProperty("TenantId", securityContext?.TenantId);

            try
            {
                var result = await mongoCommand();
                activity?.AddTag("Status", "Success");
                return result;
            }
            catch (Exception ex)
            {
                activity?.AddTag("Status", "Failure");
                activity?.AddTag("ExceptionMessage", ex.Message);
                throw;
            }
            finally
            {
                activity?.Stop();
            }
        }

        private IMongoDatabase SaveNewTenantDbConnection(string tenantId)
        {
            try
            {
                var (dbName, dbConnection) = _tenants.GetTenantDatabaseConnectionString(tenantId);

                if (string.IsNullOrWhiteSpace(dbConnection))
                {
                    throw new KeyNotFoundException($"Database Connection string is not found for {tenantId}");
                }

                var database = new MongoClient(dbConnection).GetDatabase(dbName);
                _databases.Add(tenantId, database);

                return database;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
                return null;
            }
        }
    }
}
