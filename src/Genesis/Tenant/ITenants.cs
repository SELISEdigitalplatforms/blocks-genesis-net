namespace Blocks.Genesis
{
    /// <summary>
    /// Interface for tenant management operations
    /// </summary>
    public interface ITenants
    {
        /// <summary>
        /// Gets a tenant by its ID
        /// </summary>
        /// <param name="tenantId">The tenant ID to look up</param>
        /// <returns>The tenant if found, null otherwise</returns>
        Tenant? GetTenantByID(string tenantId);
    }
}