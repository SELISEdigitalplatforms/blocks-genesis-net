using MongoDB.Driver;

namespace Blocks.Genesis
{
    public class Tenants : ITenants
    {
        private readonly IEnumerable<Tenant> _tenants;
        public Tenants()
        {
            IMongoDatabase database = new MongoClient("mongodb://localhost:27017").GetDatabase("Blocks");
            _tenants = database.GetCollection<Tenant>("Tenants").Find((Tenant _) => true).ToEnumerable();
        }
        public Tenant GetTenantByApplicationDomain(string applicationDomain)
        {
            return _tenants.FirstOrDefault((Tenant t) => t.ApplicationDomain.Equals(applicationDomain, StringComparison.InvariantCultureIgnoreCase));
        }

        public Tenant GetTenantByID(string tenantId)
        {
            return _tenants.FirstOrDefault((Tenant t) => t.TenantId.Equals(tenantId, StringComparison.InvariantCultureIgnoreCase));
        }

        public string GetTenantDatabaseConnectionString(string tenantId)
        {
            var tenant = _tenants.FirstOrDefault((Tenant t) => t.TenantId.Equals(tenantId, StringComparison.InvariantCultureIgnoreCase));
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
                Audiences = new[] { "audience1" },
                SigningKeyPassword = "signingKey1",
                SigningKeyPath = ""
            };
        }

        public IEnumerable<JwtTokenParameters> GetTenantTokenValidationParameters()
        {
            return _tenants.Select((Tenant t) =>  new JwtTokenParameters
            {
                Issuer = "https://issuer1.com",
                Audiences = new[] { "audience1" },
                SigningKeyPassword = "signingKey1",
                SigningKeyPath = ""
            });
        }
    }
}
