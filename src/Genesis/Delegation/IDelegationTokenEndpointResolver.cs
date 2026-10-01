namespace Blocks.Genesis;

/// <summary>
/// Resolves IAM's token endpoint. Callers never know the URL: route paths are rewritten by
/// <c>ApiRoutePrefixConvention</c>, so the effective path is <c>{base}/{prefix}/oidc/token</c>
/// and may never be hardcoded.
/// </summary>
public interface IDelegationTokenEndpointResolver
{
    /// <summary>Returns the token endpoint for a tenant, preferring OIDC discovery.</summary>
    Task<string> GetTokenEndpointAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Throws when neither <c>BLOCKS_IAM_BASE_URL</c> nor <c>BLOCKS_IAM_TOKEN_ENDPOINT</c> is
    /// configured. Called at startup so a misconfigured deployment fails fast.
    /// </summary>
    void EnsureConfigured();
}
