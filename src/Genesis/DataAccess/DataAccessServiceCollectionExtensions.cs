using Blocks.Genesis.DataAccess.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Blocks.Genesis.DataAccess;

/// <summary>Options for <see cref="DataAccessServiceCollectionExtensions.AddBlocksDataAccess"/>.</summary>
public sealed class DataAccessOptions
{
    /// <summary>
    /// When true (default for the registration path) the by-name resolver loads only schemas with
    /// <c>IsSystemEntity == true</c>; a tenant's own same-name schema counts as missing (KeyNotFoundException).
    /// Set false for hosts that serve tenant schemas.
    /// </summary>
    public bool SystemEntitiesOnly { get; set; } = true;

    /// <summary>The default for <see cref="RolePermissionCacheDuration"/>: 60 seconds.</summary>
    public static readonly TimeSpan DefaultRolePermissionCacheDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long role-bound permissions (the <c>Permissions</c> Auth value) are kept in memory per tenant,
    /// organization and role set. A role or permission change reaches policy evaluation within this time.
    /// </summary>
    public TimeSpan RolePermissionCacheDuration { get; set; } = DefaultRolePermissionCacheDuration;
}

public static class DataAccessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DataAccessPolicy engine. Opt-in: nothing in the Genesis bootstrap calls this.
    /// <para>
    /// <see cref="IDataAccessResolver"/> resolves by schema name, reading <c>SchemaDefinitions</c>,
    /// <c>DataAccessPolicys</c> and <c>DataValidations</c> from the caller's tenant database through
    /// <see cref="IDbContextProvider"/>. <see cref="IDataAccessPolicyResolutionService"/> evaluates an already loaded schema.
    /// </para>
    /// <para>
    /// The caller comes from <see cref="IAccessPrincipalAccessor"/>. If the host registers none, the default
    /// reads <see cref="BlocksContext"/> and never treats the caller as the root tenant, so no request bypasses
    /// the policies unless the host says so with its own accessor.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBlocksDataAccess(this IServiceCollection services, Action<DataAccessOptions>? configure = null)
    {
        var options = new DataAccessOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IAccessPolicySource, SchemaPolicySource>();
        services.AddSingleton<IDataAccessPolicyResolutionService, DataAccessPolicyResolutionService>();
        services.AddSingleton<IDataAccessResolver, DataAccessResolver>();
        services.TryAddSingleton<IAccessPrincipalEnricher, AccessPrincipalEnricher>();
        services.TryAddSingleton<IAccessPrincipalAccessor, BlocksContextAccessPrincipalAccessor>();
        return services;
    }
}

/// <summary>The default caller: the current <see cref="BlocksContext"/>, never the root tenant.</summary>
public sealed class BlocksContextAccessPrincipalAccessor : IAccessPrincipalAccessor
{
    public AccessPrincipal Current => AccessPrincipal.From(BlocksContext.GetContext(), isRootTenant: false);
}
