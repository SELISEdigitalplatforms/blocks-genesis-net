using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Blocks.Genesis
{
    public class Tenants : ITenants
    {
        private List<Tenant> _tenants = new List<Tenant>();
        private readonly ILogger<Tenants> _logger;
        private readonly IBlocksSecret _blocksSecret;
        private readonly ICacheClient _cacheClient;
        private readonly IMongoDatabase _database;


        public Tenants(ILogger<Tenants> logger, IBlocksSecret blocksSecret, ICacheClient cacheClient)
        {
            _logger = logger;
            _blocksSecret = blocksSecret;
            _cacheClient = cacheClient;

            CacheTenants();
        }

        public Tenant? GetTenantByID(string tenantId)
        {
            var tenant = _tenants.FirstOrDefault(t => t.ItemId == tenantId || t.TenantId == tenantId) ?? GetTenantFromDb(tenantId);

            return tenant;
        }

        public Dictionary<string, (string, string)> GetTenantDatabaseConnectionStrings()
        {
            return _tenants.ToDictionary(t => t.TenantId, t => (t.DBName, t.DbConnectionString));
        }

        public (string?, string?) GetTenantDatabaseConnectionString(string tenantId)
        {
            var tenant = _tenants?.FirstOrDefault(t => t.TenantId == tenantId);

            if (tenant == null)
            {
                tenant = GetTenantFromDb(tenantId);
            }

            return (tenant?.DBName, tenant?.DbConnectionString);
        }

        public JwtTokenParameters? GetTenantTokenValidationParameter(string tenantId)
        {
            var tenant = _tenants.FirstOrDefault((Tenant t) => t.TenantId.Equals(tenantId, StringComparison.InvariantCultureIgnoreCase));
            return tenant == null ? GetTenantFromDb(tenantId)?.JwtTokenParameters : tenant.JwtTokenParameters;
        }

        public void CacheTenants()
        {
            try
            {
                IMongoDatabase _database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RootDatabaseName);
                _tenants = _database.GetCollection<Tenant>(BlocksConstants.TenantCollectionName).Find(_ => true).ToList();

                foreach (var tenant in _tenants)
                {
                    LmtConfiguration.CreateCollectionForTrace(_blocksSecret.TraceConnectionString, tenant.TenantId);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
            }


        }

        private Tenant? GetTenantFromDb(string tenantId)
        {
            return _database.GetCollection<Tenant>(BlocksConstants.TenantCollectionName).Find((Tenant t) => t.ItemId == tenantId || t.TenantId == tenantId).FirstOrDefault();
        }

    }
}
