namespace Blocks.Genesis;

/// <summary>
/// Redeems the ambient delegation grant for a short-lived Blocks access token carrying user context.
/// </summary>
public interface IDelegatedTokenProvider
{
    /// <summary>
    /// Returns a valid access token for the grant in <see cref="DelegatedTokenContext"/>, or
    /// <c>null</c> when there is no grant or it can no longer be redeemed. Cached and
    /// single-flighted per grant id, so a long job may call this as often as it likes.
    /// </summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Builds the headers for one outbound call: <c>Authorization: Bearer</c> plus
    /// <c>x-blocks-key</c>. Pass the result to <c>IHttpService</c> so the call is still traced.
    /// <para>
    /// Delegation is opt-in per call, by design. Nothing attaches a delegated token to a request
    /// you did not ask about — a worker calling a third party must not hand it a Blocks credential.
    /// </para>
    /// <para>
    /// Returns <paramref name="existingHeaders"/> unchanged (as a copy) when there is no grant in
    /// scope, when it cannot be redeemed, or when the caller already supplied an
    /// <c>Authorization</c> header.
    /// </para>
    /// </summary>
    Task<Dictionary<string, string>> GetAuthorizationHeadersAsync(
        Dictionary<string, string>? existingHeaders = null,
        CancellationToken ct = default);

    /// <summary>Drops the cached token for a grant. Called when a message settles.</summary>
    void Invalidate(string? delegationGrantId);
}
