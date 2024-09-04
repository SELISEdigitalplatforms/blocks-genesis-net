using MongoDB.Driver;
using StackExchange.Redis;

namespace Blocks.Genesis
{
    public class Tenants : ITenants
    {
        private List<Tenant> _tenants = new List<Tenant>();
        private readonly IBlocksSecret _blocksSecret;
        private readonly ICacheClient _cacheClient;

        public Tenants(IBlocksSecret blocksSecret, ICacheClient cacheClient)
        {
            _blocksSecret = blocksSecret;
            IMongoDatabase database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RooDatabaseName);
            _cacheClient = cacheClient;
            _tenants = database.GetCollection<Tenant>("Tenants").Find((Tenant _) => true).ToList();
            CacheTenantInfo(_tenants);
        }

        public Tenant? GetTenantByApplicationDomain(string applicationDomain)
        {
            return _tenants.FirstOrDefault(t => t.ApplicationDomain.Equals(applicationDomain, StringComparison.InvariantCultureIgnoreCase));
        }

        public Tenant? GetTenantByID(string tenantId)
        {
            return _tenants.FirstOrDefault(t => t.TenantId.Equals(tenantId, StringComparison.InvariantCultureIgnoreCase));
        }

        public string GetTenantDatabaseConnectionString(string tenantId)
        {
            var tenant =  GetTenantByID(tenantId);
            return tenant?.DbConnectionString ?? string.Empty;
        }

        public Dictionary<string, string> GetTenantDatabaseConnectionStrings()
        {
            return _tenants.ToDictionary(t => t.TenantId, t => t.DbConnectionString);
        }

        public JwtTokenParameters? GetTenantTokenValidationParameter(string tenantId)
        {
            var tenant = _tenants.FirstOrDefault((Tenant t) => t.TenantId.Equals(tenantId, StringComparison.InvariantCultureIgnoreCase));
            if (tenant == null) return null;

            return new JwtTokenParameters 
            {
                Issuer = "https://issuer1.com",
                Audiences = new List<string> { "audience1" },
                SigningKeyPassword = "signingKey1",
                SigningKeyPath = ""
            };
        }

        public IEnumerable<JwtTokenParameters> GetTenantTokenValidationParameters()
        {
            return _tenants.Select((Tenant t) =>  new JwtTokenParameters
            {
                Issuer = "https://issuer1.com",
                Audiences = new List<string> { "audience1" },
                SigningKeyPassword = "signingKey1",
                SigningKeyPath = ""
            });
        }

        public async Task ReloadTenantsAsync()
        {
            IMongoDatabase database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RooDatabaseName);
            _tenants = await database.GetCollection<Tenant>("Tenants").Find((Tenant _) => true).ToListAsync();
            CacheTenantInfo(_tenants);
        }

        private void CacheTenantInfo(List<Tenant> tenants)
        {
            foreach (var tenant in tenants)
            {
                _cacheClient.AddStringValue($"tenant-origin-{tenant.TenantId}", tenant.ApplicationDomain);
            }
        }
    }
}
