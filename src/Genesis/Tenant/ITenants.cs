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

        /// <summary>
        /// Gets all tenant database connection strings
        /// </summary>
        /// <returns>Dictionary mapping tenant IDs to tuples of (DB name, connection string)</returns>
        Dictionary<string, (string, string)> GetTenantDatabaseConnectionStrings();

        /// <summary>
        /// Gets a specific tenant's database connection string
        /// </summary>
        /// <param name="tenantId">The tenant ID</param>
        /// <returns>Tuple of (DB name, connection string) if found, (null, null) otherwise</returns>
        (string?, string?) GetTenantDatabaseConnectionString(string tenantId);

        /// <summary>
        /// Gets JWT token validation parameters for a tenant
        /// </summary>
        /// <param name="tenantId">The tenant ID</param>
        /// <returns>JWT token parameters if found, null otherwise</returns>
        JwtTokenParameters? GetTenantTokenValidationParameter(string tenantId);

        /// <summary>
        /// Updates the tenant cache by checking for version changes
        /// </summary>
        void UpdateTenantCache();

        /// <summary>
        /// Updates the tenant version in cache and notifies all instances
        /// </summary>
        void UpdateTenantVersion();

        /// <summary>
        /// Updates the tenant version in cache and notifies all instances asynchronously
        /// </summary>
        /// <returns>A task representing the asynchronous operation</returns>
        Task UpdateTenantVersionAsync();
    }
}