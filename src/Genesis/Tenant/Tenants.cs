using MongoDB.Driver;
using StackExchange.Redis;

namespace Blocks.Genesis
{
    public class Tenants : ITenants
    {
        private readonly List<Tenant> _tenants = new List<Tenant>();
        private readonly IBlocksSecret _blocksSecret;

        public Tenants(IBlocksSecret blocksSecret)
        {
            _blocksSecret = blocksSecret;
            IMongoDatabase database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RooDatabaseName);
            _tenants = database.GetCollection<Tenant>("Tenants").Find((Tenant _) => true).ToList(); 
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
    }
}
