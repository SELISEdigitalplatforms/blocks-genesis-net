namespace Blocks.Genesis
{
    public interface ITenants
    {
        Tenant? GetTenantByID(string tenantId);
        Dictionary<string, (string, string)> GetTenantDatabaseConnectionStrings();
        (string?, string?) GetTenantDatabaseConnectionString(string tenantId);
        JwtTokenParameters? GetTenantTokenValidationParameter(string tenantId);
    }
}
