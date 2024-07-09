using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Newtonsoft.Json;

namespace Blocks.Genesis
{
    internal class MongoDbContextProvider : IDbContextProvider
    {
        private readonly IDictionary<string, IMongoDatabase> _databases = new SortedDictionary<string, IMongoDatabase>();
        private readonly ILogger<MongoDbContextProvider> _logger;

        public MongoDbContextProvider(ILogger<MongoDbContextProvider> logger, ITenants tenants)
        {
            _logger = logger;

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
    }
}
