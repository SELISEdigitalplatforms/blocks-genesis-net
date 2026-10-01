namespace Blocks.Genesis;

/// <summary>
/// Wire-level constants for delegated access (RFC 8693 token exchange).
/// These values are part of the cross-SDK contract: blocks-genesis-py must
/// match them byte for byte.
/// </summary>
public static class DelegationConstants
{
    /// <summary>Message header (ApplicationProperties / AMQP header) carrying the opaque grant id.</summary>
    public const string DelegationGrantHeader = "DelegationGrant";

    /// <summary>Redis key prefix for the grant record.</summary>
    public const string GrantKeyPrefix = "delegation:";

    /// <summary>Redis key prefix for single-use exchange nonces.</summary>
    public const string NonceKeyPrefix = "nonce:";

    /// <summary>Redis key prefix for the per-grant redemption rate counter.</summary>
    public const string RedemptionKeyPrefix = "redemption:";

    /// <summary>Opaque grant id prefix. Ids are <c>dg_</c> + 64 lowercase hex chars.</summary>
    public const string GrantIdPrefix = "dg_";

    /// <summary>Number of cryptographically random bytes behind a grant id.</summary>
    public const int GrantIdRandomBytes = 32;

    /// <summary>Number of cryptographically random bytes behind an exchange nonce.</summary>
    public const int NonceRandomBytes = 16;

    /// <summary>RFC 8693 grant type.</summary>
    public const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>Blocks-specific subject token type naming the delegation grant.</summary>
    public const string DelegationGrantTokenType = "urn:blocks:params:oauth:token-type:delegation-grant";

    /// <summary>Default absolute lifetime of a grant record.</summary>
    public static readonly TimeSpan DefaultGrantTtl = TimeSpan.FromDays(2);

    /// <summary>Nonce replay-window TTL. Must be at least twice the clock window.</summary>
    public static readonly TimeSpan NonceTtl = TimeSpan.FromSeconds(120);

    /// <summary>Accepted clock skew on the <c>ts</c> field, in seconds.</summary>
    public const int ClockWindowSeconds = 60;

    /// <summary>Named <see cref="HttpClient"/> used for the exchange call. Deliberately has no delegated-token handler.</summary>
    public const string ExchangeHttpClientName = "blocks-delegation-exchange";

    /// <summary>How long before a token's <c>exp</c> a cached entry stops being served.</summary>
    public static readonly TimeSpan TokenRenewalMargin = TimeSpan.FromMinutes(1);

    // Configuration keys. Resolution order for both is
    // environment variable -> configuration root -> FrontendRuntime section.
    public const string IamBaseUrlKey = "BLOCKS_IAM_BASE_URL";
    public const string IamTokenEndpointKey = "BLOCKS_IAM_TOKEN_ENDPOINT";
    public const string FrontendRuntimeSection = "FrontendRuntime";

    /// <summary>Builds the pipe-delimited signature input. Exact field order, no whitespace.</summary>
    public static string BuildSignatureInput(string tenantId, string delegationId, string nonce, long ts)
        => $"{tenantId}|{delegationId}|{nonce}|{ts}";
}
