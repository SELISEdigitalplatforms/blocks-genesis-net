namespace Blocks.Genesis;

/// <summary>
/// Sessions signed out before their access tokens expired. IAM marks a session here when a user or an
/// administrator signs it out ("Sign out of this device"). The token check of every service then refuses
/// an access token whose <c>sid</c> claim names a marked session, so the sign-out takes effect on the next
/// request instead of when the access token expires. A mark only has to outlive the longest access token
/// that can carry the session id.
/// </summary>
public static class RevokedSessions
{
    /// <summary>Prefix of the cache key that marks a revoked session.</summary>
    public const string KeyPrefix = "revoked_session:";

    /// <summary>Cache key that marks <paramref name="sessionId"/> as revoked.</summary>
    public static string KeyFor(string sessionId) => KeyPrefix + sessionId;

    /// <summary>
    /// Marks <paramref name="sessionId"/> as revoked for <paramref name="lifetime"/>. Returns false without
    /// touching the cache when the session id is empty or the lifetime is not positive.
    /// </summary>
    public static Task<bool> MarkAsync(ICacheClient cache, string? sessionId, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (string.IsNullOrWhiteSpace(sessionId) || lifetime <= TimeSpan.Zero)
        {
            return Task.FromResult(false);
        }

        return cache.AddStringValueAsync(KeyFor(sessionId), "1", (long)Math.Ceiling(lifetime.TotalSeconds));
    }

    /// <summary>True when <paramref name="sessionId"/> is marked as revoked. An empty session id never is.</summary>
    public static async Task<bool> IsRevokedAsync(ICacheClient cache, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        return await cache.KeyExistsAsync(KeyFor(sessionId)).ConfigureAwait(false);
    }
}
