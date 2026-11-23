using System.Globalization;

namespace Blocks.Genesis;

/// <summary>
/// Times after which access tokens must be minted again. IAM stamps a user when it takes a role,
/// permission or organization away from them, an organization when it is disabled, and a sign-in
/// session when it switches organization. The token check of every service then refuses an access
/// token issued before a stamp that applies to it, so the change takes effect on the next request
/// instead of when the access token expires. The client refreshes, and the refresh re-reads the
/// user's current membership. A stamp only has to outlive the longest access token.
/// </summary>
public static class AccessStamps
{
    /// <summary>Prefix of the stamp set when a user loses access (key: prefix + tenant + ":" + user).</summary>
    public const string UserPrefix = "access_changed:";

    /// <summary>Prefix of the stamp set when an organization is disabled (key: prefix + tenant + ":" + organization).</summary>
    public const string OrganizationPrefix = "org_disabled:";

    /// <summary>Prefix of the stamp set when a sign-in session switches organization (key: prefix + sid).</summary>
    public const string SessionPrefix = "org_switched:";

    private const string DefaultOrganizationId = "default";

    public static string UserKey(string tenantId, string userId) => $"{UserPrefix}{tenantId}:{userId}";

    public static string OrganizationKey(string tenantId, string organizationId) => $"{OrganizationPrefix}{tenantId}:{organizationId}";

    public static string SessionKey(string sessionId) => SessionPrefix + sessionId;

    /// <summary>
    /// Stamps <paramref name="key"/> with the current time for <paramref name="lifetime"/>. Returns false
    /// without touching the cache when the key is empty or the lifetime is not positive.
    /// </summary>
    public static Task<bool> StampAsync(ICacheClient cache, string key, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (string.IsNullOrWhiteSpace(key) || lifetime <= TimeSpan.Zero)
        {
            return Task.FromResult(false);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return cache.AddStringValueAsync(key, now.ToString(CultureInfo.InvariantCulture), (long)Math.Ceiling(lifetime.TotalSeconds));
    }

    /// <summary>
    /// True when a stamp that applies to the token is later than the token's issue time. A token issued
    /// in the same second as a stamp passes: the refresh that answers a stamp is minted in that second.
    /// </summary>
    public static async Task<bool> IsStaleAsync(ICacheClient cache, AccessStampSubject subject)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(subject);

        var keys = KeysFor(subject);
        if (keys.Count == 0)
        {
            return false;
        }

        var stamps = await Task.WhenAll(keys.Select(cache.GetStringValueAsync)).ConfigureAwait(false);
        return stamps.Any(stamp => long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stampedAt)
                                   && stampedAt > subject.IssuedAt);
    }

    private static List<string> KeysFor(AccessStampSubject subject)
    {
        var keys = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(subject.TenantId) && !string.IsNullOrWhiteSpace(subject.UserId))
        {
            keys.Add(UserKey(subject.TenantId, subject.UserId));
        }

        if (!string.IsNullOrWhiteSpace(subject.TenantId)
            && !string.IsNullOrWhiteSpace(subject.OrganizationId)
            && !string.Equals(subject.OrganizationId, DefaultOrganizationId, StringComparison.Ordinal))
        {
            keys.Add(OrganizationKey(subject.TenantId, subject.OrganizationId));
        }

        if (!string.IsNullOrWhiteSpace(subject.SessionId))
        {
            keys.Add(SessionKey(subject.SessionId));
        }

        return keys;
    }
}

/// <summary>What a token says about who it is for. <see cref="IssuedAt"/> is the token's <c>iat</c> in Unix seconds.</summary>
public sealed record AccessStampSubject(string? TenantId, string? UserId, string? OrganizationId, string? SessionId, long IssuedAt);
