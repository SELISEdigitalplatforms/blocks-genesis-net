namespace Blocks.Genesis
{
    public interface ITenants
    {
        public Task<Tenant> GetTenantByID(string tenantId);
        public Task<Tenant> GetTenantByApplicationDomain(string tenantName);
        public Task<string> GetTenantDatabaseConnectionString(string tenantId);
        public Dictionary<string, string> GetTenantDatabaseConnectionStrings();
        public JwtTokenParameters GetTenantTokenValidationParameter(string tenantId);
        public IEnumerable<JwtTokenParameters> GetTenantTokenValidationParameters();
    }
}
