namespace Blocks.Genesis
{
    public interface ITenants
    {
        public Tenant GetTenantByID(string tenantId);
        public Tenant GetTenantByApplicationDomain(string tenantName);
        public string GetTenantDatabaseConnectionString(string tenantId);
        public Dictionary<string, string> GetTenantDatabaseConnectionStrings();
        public JwtTokenParameters GetTenantTokenValidationParameter(string tenantId);
        public IEnumerable<JwtTokenParameters> GetTenantTokenValidationParameters();
    }
}
