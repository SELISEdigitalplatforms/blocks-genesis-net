namespace Blocks.Genesis;

public static class BlocksConstants
{
    internal const string TenantCollectionName = "Tenants";
    internal const string TenantTokenPublicCertificateCachePrefix = "tetocertpublic::";

    /// <summary>
    /// Cache slot for one external provider's public certificate, keyed by tenant and provider.
    /// </summary>
    /// <remarks>
    /// Scoped per provider rather than per tenant, unlike
    /// <see cref="TenantTokenPublicCertificateCachePrefix"/>: a tenant may trust several external
    /// providers at once, and a tenant-wide slot would hand one provider's certificate to another.
    /// </remarks>
    internal const string ThirdPartyProviderCertificateCachePrefix = "tpprovcert::";
    internal const string ThirdPartyContextHeader = "ThirdPartyContext";
    public const string BlocksKey = "x-blocks-key";

    /// <summary>
    /// Names which external identity provider a token came from, for the one case issuer and
    /// audience cannot separate: two providers configured with both the same.
    /// </summary>
    public const string ThirdPartyIdpHeader = "x-blocks-idp";
    public const string BlocksGrpcKey = "x-blocks-service-key";
    public const string ProjectContextIdHeader = "x-context-id";
    public const string AuthorizationHeaderName = "Authorization";
    public const string Bearer = "Bearer ";
    public const string Miscellaneous = "miscellaneous";
    internal const string KeyVault = "KeyVault";
    internal const string ProtectedResourceName = "ProtectedResourceName";

    /// <summary>
    /// Seconds a subject is blocked after going over its rate limit. Sent as <c>Retry-After</c>.
    /// </summary>
    public const int RateLimitRetryAfterSeconds = 30;

    /// <summary>
    /// Requests per second allowed per subject when neither the environment nor the vault sets a valid limit.
    /// </summary>
    public const int DefaultUserRateLimitPerSecond = 100;

    /// <summary>
    /// Length of one rate-limit window, in seconds.
    /// </summary>
    public const int RateLimitWindowSeconds = 1;

    /// <summary>
    /// Upper bound for one Redis rate-limit check. Past this the request is allowed.
    /// </summary>
    public const int RateLimitRedisTimeoutMilliseconds = 50;

    /// <summary>
    /// Environment variable that overrides the per-subject limit for one service.
    /// </summary>
    public const string UserRateLimitEnvironmentVariable = "UserRateLimitPerSecond";

    /// <summary>
    /// Prefix of every rate-limit Redis key: <c>ratelimit:{service}:{tenant}:{type}:{id}</c>.
    /// </summary>
    public const string RateLimitKeyPrefix = "ratelimit";

}


