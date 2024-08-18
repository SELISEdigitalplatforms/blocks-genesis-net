using MongoDB.Driver;
using StackExchange.Redis;

namespace Blocks.Genesis
{
    public class Tenants : ITenants
    {
        private readonly List<Tenant> _tenants = new List<Tenant>();
        private readonly IMongoCollection<Tenant> _collection;
        private readonly IBlocksSecret _blocksSecret;

        public Tenants(IBlocksSecret blocksSecret)
        {
            _blocksSecret = blocksSecret;
            IMongoDatabase database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase("Blocks");
            _collection = database.GetCollection<Tenant>("Tenants");
            _tenants = _collection.Find((Tenant _) => true).ToList();  
        }
        public async Task<Tenant> GetTenantByApplicationDomain(string applicationDomain)
        {
            var tenant = _tenants.FirstOrDefault((Tenant t) => t.ApplicationDomain.Equals(applicationDomain, StringComparison.InvariantCultureIgnoreCase));

            if (tenant == null)
            {
                tenant = await _collection.Find(t => t.ApplicationDomain == applicationDomain).FirstOrDefaultAsync();
                _tenants.Add(tenant);
            }

            return tenant;
        }

        public async Task<Tenant> GetTenantByID(string tenantId)
        {
            var tenant = _tenants.FirstOrDefault((Tenant t) => t.TenantId.Equals(tenantId, StringComparison.InvariantCultureIgnoreCase));

            if (tenant == null)
            {
                tenant = await _collection.Find(t => t.TenantId == tenantId).FirstOrDefaultAsync();
                _tenants.Add(tenant);
            }

            return tenant;
        }

        public async Task<string> GetTenantDatabaseConnectionString(string tenantId)
        {
            var tenant = await GetTenantByID(tenantId);

            return tenant?.DbConnectionString ?? string.Empty;
        }

        public Dictionary<string, string> GetTenantDatabaseConnectionStrings()
        {
            return _tenants.ToDictionary(t => t.TenantId, t => t.DbConnectionString);
        }

        public JwtTokenParameters GetTenantTokenValidationParameter(string tenantId)
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
    }
}
