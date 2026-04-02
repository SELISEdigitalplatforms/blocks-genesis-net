namespace Blocks.Genesis
{
    internal interface ITenantLookup : ITenants
    {
        Tenant? GetTenantByApplicationDomain(string appName);
    }
}