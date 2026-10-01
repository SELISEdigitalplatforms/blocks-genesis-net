namespace Blocks.Genesis;

/// <summary>
/// How the quota client behaves. Limits themselves come from blocks-os; these are the knobs that
/// decide what happens when the counter cannot answer.
/// </summary>
public sealed class QuotaOptions
{
    /// <summary>
    /// Hard ceiling on how long a quota check may take before the fail mode applies. Without it a
    /// sick Redis does not merely break metering — it becomes the latency floor of every request.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>What an unlisted meter does when the counter is unreachable.</summary>
    public QuotaFailMode DefaultFailMode { get; set; } = QuotaFailMode.Open;

    /// <summary>
    /// Meters whose every unit is money that cannot be recovered — GPU time, CI minutes. These fail
    /// closed: an outage that lets them run free is a direct loss, where a bounded undercount of
    /// cheap counters is not.
    /// </summary>
    public HashSet<string> FailClosedMeters { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai.blocksCredits",
        "studio.blocksCredits",
        "release.builds"
    };

    /// <summary>
    /// How often counts are carried from Redis to Mongo. This is your maximum acceptable loss if
    /// Redis dies: everything since the last flush is gone, and only meters with a domain record
    /// behind them can be recounted.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Kills enforcement without a deploy. Everything is allowed and nothing is recorded.</summary>
    public bool Disabled { get; set; }

    /// <summary>Kills enforcement for named meters only.</summary>
    public HashSet<string> DisabledMeters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long an idempotency key is remembered. Longer than any sane retry window.</summary>
    public TimeSpan IdempotencyWindow { get; set; } = TimeSpan.FromHours(26);

    /// <summary>
    /// Backstop expiry on a counter key. Rollover is driven by blocks-os writing the next period;
    /// this only stops an abandoned tenant's keys living forever.
    /// </summary>
    public TimeSpan CounterTtl { get; set; } = TimeSpan.FromDays(35);

    public QuotaFailMode FailModeFor(string meter) =>
        FailClosedMeters.Contains(meter) ? QuotaFailMode.Closed : DefaultFailMode;

    public bool IsEnforced(string meter) => !Disabled && !DisabledMeters.Contains(meter);
}
