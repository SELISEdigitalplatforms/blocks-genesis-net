namespace Blocks.Genesis;

/// <summary>
/// Writes and removes delegation grants. A grant is created at send time, while a validated
/// user token is still in scope, and removed after the worker has settled the message.
/// </summary>
public interface IDelegationGrantStore
{
    /// <summary>
    /// Persists a grant and returns its opaque id (<c>dg_</c> + 64 lowercase hex chars).
    /// </summary>
    /// <param name="ctx">Context supplying tenant, user and organization. Must carry an authenticated user.</param>
    /// <param name="tokenVersion">The user's <c>token_version</c> claim at send time.</param>
    /// <param name="securityStamp">The user's <c>security_stamp</c> claim at send time.</param>
    /// <param name="ttl">Absolute lifetime. Defaults to <see cref="DelegationConstants.DefaultGrantTtl"/>.</param>
    Task<string> CreateAsync(BlocksContext ctx, string tokenVersion, string securityStamp, TimeSpan? ttl = null);

    /// <summary>Best-effort removal after a successful settle. Never called before the ACK.</summary>
    Task DeleteAsync(string id);

    /// <summary>
    /// Reads a grant record. Used only for chained delegation: a worker-originated send carries
    /// <c>TokenVersion</c> and <c>SecurityStamp</c> forward from the grant it is already holding.
    /// </summary>
    Task<DelegationGrantRecord?> GetAsync(string id);
}
