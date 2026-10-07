using System.Security.Claims;

namespace Blocks.Genesis;

/// <summary>
/// The caller a request is counted against, taken only from the authenticated token's claims.
/// </summary>
internal readonly record struct RateLimitSubject(string Type, string Id, string TenantId)
{
    public const string UserType = "user";
    public const string ClientType = "client";

    /// <summary>
    /// Resolves the subject from <c>user_id</c>, then <c>client_id</c>. Request headers are never read.
    /// </summary>
    public static bool TryResolve(ClaimsPrincipal? principal, out RateLimitSubject subject)
    {
        subject = default;

        if (principal?.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return false;
        }

        var tenantId = identity.FindFirst(BlocksContext.TENANT_ID_CLAIM)?.Value ?? string.Empty;

        var userId = identity.FindFirst(BlocksContext.USER_ID_CLAIM)?.Value;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            subject = new RateLimitSubject(UserType, userId, tenantId);
            return true;
        }

        var clientId = identity.FindFirst(BlocksContext.CLIENT_ID_CLAIM)?.Value;
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            subject = new RateLimitSubject(ClientType, clientId, tenantId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>ratelimit:{serviceName}:{tenantId}:{type}:{id}</c>. An empty tenant is written as <c>-</c>.
    /// </summary>
    public string BuildKey(string serviceName)
    {
        var tenant = string.IsNullOrWhiteSpace(TenantId) ? "-" : TenantId;
        return $"{BlocksConstants.RateLimitKeyPrefix}:{serviceName}:{tenant}:{Type}:{Id}";
    }
}
