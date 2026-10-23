using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess;

/// <summary>
/// The caller as seen by policy evaluation: exactly the BlocksContext fields the evaluator reads
/// (see <see cref="DataAccessPolicyHelper.GetTokenValue"/>) plus the root-tenant flag the gateway
/// resolves per request. Captured once at the request boundary instead of read ambiently inside the
/// evaluation code, so any host can supply it.
/// </summary>
public sealed record AccessPrincipal(
    string? UserId,
    string? UserName,
    string? TenantId,
    string? OrganizationId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool IsAuthenticated,
    bool IsRootTenant)
{
    /// <summary>
    /// True when the principal was built from a BlocksContext. A missing context makes every token
    /// value resolve to null, which is what the evaluator did when BlocksContext.GetContext() was null.
    /// </summary>
    public bool HasContext { get; init; }

    /// <summary>
    /// Permissions bound to the caller's roles in their organization, read from the tenant database per request
    /// (<see cref="IAccessPrincipalEnricher"/>). Empty until enriched.
    /// </summary>
    public IReadOnlyList<string> RolePermissions { get; init; } = [];

    /// <summary>
    /// The caller's access attributes in their organization (<c>User.Attributes["access"][org][name]</c>), read
    /// from the tenant database per request. Null until enriched; never taken from token claims.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? AccessAttributes { get; init; }

    /// <summary>What the <c>Permissions</c> Auth value resolves to: token permissions plus role-bound permissions.</summary>
    public IReadOnlyList<string> EffectivePermissions =>
        RolePermissions.Count == 0
            ? Permissions
            : Permissions.Concat(RolePermissions).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The organization policies are evaluated for: a blank organization is <c>default</c>.</summary>
    public string EffectiveOrganizationId =>
        string.IsNullOrWhiteSpace(OrganizationId) ? DefaultOrganizationId : OrganizationId;

    /// <summary>The tenant-wide organization.</summary>
    public const string DefaultOrganizationId = "default";

    /// <summary>No BlocksContext at all: every token value is null, nothing is authenticated.</summary>
    public static AccessPrincipal Anonymous { get; } = new(null, null, null, null, [], [], false, false);

    /// <summary>Builds the principal from a BlocksContext using the same field mapping the evaluator always used.</summary>
    public static AccessPrincipal From(BlocksContext? context, bool isRootTenant)
    {
        if (context is null)
            return Anonymous with { IsRootTenant = isRootTenant };

        return new AccessPrincipal(
            context.UserId,
            context.UserName,
            context.TenantId,
            context.OrganizationId,
            context.Roles?.ToArray() ?? Array.Empty<string>(),
            context.Permissions?.ToArray() ?? Array.Empty<string>(),
            context.IsAuthenticated,
            isRootTenant)
        { HasContext = true };
    }
}
