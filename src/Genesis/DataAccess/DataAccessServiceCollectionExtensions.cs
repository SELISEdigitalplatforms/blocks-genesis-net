using Blocks.Genesis.DataAccess.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Blocks.Genesis.DataAccess;

public static class DataAccessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DataAccessPolicy engine. Opt-in: nothing in the Genesis bootstrap calls this.
    /// <para>
    /// <see cref="IDataAccessResolver"/> resolves by schema name, reading <c>SchemaDefinitions</c>,
    /// <c>DataAccessPolicys</c> and <c>DataValidations</c> from the caller's tenant database through
    /// <see cref="IDbContextProvider"/>. <see cref="IDataAccessPolicyResolver"/> evaluates an already loaded schema.
    /// </para>
    /// <para>
    /// The caller comes from <see cref="IAccessPrincipalAccessor"/>. If the host registers none, the default
    /// reads <see cref="BlocksContext"/> and never treats the caller as the root tenant, so no request bypasses
    /// the policies unless the host says so with its own accessor.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBlocksDataAccess(this IServiceCollection services)
    {
        services.AddSingleton<IAccessPolicySource, SchemaPolicySource>();
        services.AddSingleton<IDataAccessPolicyResolver, DataAccessPolicyResolver>();
        services.AddSingleton<IDataAccessResolver, DataAccessResolver>();
        services.TryAddSingleton<IAccessPrincipalAccessor, BlocksContextAccessPrincipalAccessor>();
        return services;
    }
}

/// <summary>The default caller: the current <see cref="BlocksContext"/>, never the root tenant.</summary>
public sealed class BlocksContextAccessPrincipalAccessor : IAccessPrincipalAccessor
{
    public AccessPrincipal Current => AccessPrincipal.From(BlocksContext.GetContext(), isRootTenant: false);
}
