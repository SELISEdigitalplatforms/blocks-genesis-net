namespace Blocks.Genesis;

/// <summary>
/// The outcome of a single <see cref="IQuota.ConsumeAsync"/> call.
/// </summary>
public enum QuotaOutcome
{
    /// <summary>Within the ceiling. The usage has been recorded; the caller may proceed.</summary>
    Allowed = 0,

    /// <summary>The ceiling would be exceeded. Nothing was recorded and the caller must refuse.</summary>
    Denied = 1,

    /// <summary>This idempotency key has already been counted. Nothing was recorded; treat as allowed.</summary>
    Duplicate = 2,

    /// <summary>No limit is loaded for this meter. The caller seeds from the durable store and retries once.</summary>
    NotLoaded = 3,

    /// <summary>Metering does not apply — no tenant, or the root tenant. Nothing was recorded.</summary>
    Skipped = 4,

    /// <summary>The counter was unreachable and the meter fails open. Nothing was recorded.</summary>
    FailedOpen = 5,

    /// <summary>The counter was unreachable and the meter fails closed. Nothing was recorded.</summary>
    FailedClosed = 6
}

/// <summary>
/// What a meter does when the counter cannot be reached. Cheap meters let traffic through;
/// meters whose every unit is real spend do not.
/// </summary>
public enum QuotaFailMode
{
    Open = 0,
    Closed = 1
}

/// <summary>
/// The answer to one consume call, plus what is left if the counter could say.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Remaining">Units left after this call, or -1 when uncapped or unknown.</param>
/// <param name="Limit">The included ceiling, or -1 when uncapped.</param>
public readonly record struct QuotaDecision(QuotaOutcome Outcome, long Remaining, long Limit)
{
    /// <summary>True when the caller may proceed. A duplicate and a fail-open both proceed.</summary>
    public bool IsAllowed =>
        Outcome is QuotaOutcome.Allowed or QuotaOutcome.Duplicate
                or QuotaOutcome.Skipped or QuotaOutcome.FailedOpen;

    public static QuotaDecision Skipped() => new(QuotaOutcome.Skipped, -1, -1);
}

/// <summary>
/// Genesis is process: it knows how to check and record safely, once, and without becoming the
/// platform's latency floor. It never knows what a credit costs or what counts as a build —
/// the service does, and the service's own records are the source of truth.
/// </summary>
public interface IQuota
{
    /// <summary>
    /// Asks before the work happens. Checks the ceiling and records the use in one atomic step.
    /// </summary>
    /// <param name="meter">Catalogue meter id, e.g. <c>api.calls</c> or <c>ai.agents</c>.</param>
    /// <param name="amount">
    /// Units consumed. Negative gives units back — deleting an agent is the same call as creating
    /// one with the sign flipped, which is why there is no separate release method and no race.
    /// </param>
    /// <param name="idempotencyKey">
    /// The domain object's own id — <c>buildId</c>, <c>deploymentId</c>, <c>llmCallId</c>. Never a
    /// fresh GUID per attempt: a retry must arrive with the same key or it counts twice.
    /// </param>
    Task<QuotaDecision> ConsumeAsync(string meter, long amount, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a policy meter — a setting rather than a quota, such as <c>release.idleStopMinutes</c>.
    /// </summary>
    Task<long> GetPolicyAsync(string meter, CancellationToken cancellationToken = default);
}
