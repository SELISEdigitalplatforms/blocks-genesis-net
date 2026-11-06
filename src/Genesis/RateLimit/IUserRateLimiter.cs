namespace Blocks.Genesis;

/// <summary>
/// Outcome of one rate-limit check.
/// </summary>
/// <param name="Allowed">True when the request may run.</param>
/// <param name="Remaining">Requests left in the current window (0 when rejected).</param>
/// <param name="ResetSeconds">Seconds until the window resets, or until the cooldown ends when rejected.</param>
public readonly record struct RateLimitDecision(bool Allowed, int Remaining, int ResetSeconds);

/// <summary>
/// Counts requests per subject key and decides whether a request may run.
/// </summary>
public interface IUserRateLimiter
{
    /// <summary>
    /// Counts one request against <paramref name="key"/>.
    /// Returns null when the limiter could not decide (Redis down, slow, or circuit open); the caller allows the request.
    /// </summary>
    Task<RateLimitDecision?> TryAcquireAsync(string key, CancellationToken cancellationToken = default);
}
