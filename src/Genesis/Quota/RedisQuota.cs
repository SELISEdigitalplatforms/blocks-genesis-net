using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Blocks.Genesis;

/// <summary>
/// The quota client. Check and record are one operation because separating them is exactly how a
/// check ends up enforcing nothing.
/// </summary>
public sealed class RedisQuota : IQuota
{
    private readonly ICacheClient _cache;
    private readonly IQuotaLimitStore _store;
    private readonly ITenants _tenants;
    private readonly QuotaFlushTracker _tracker;
    private readonly QuotaOptions _options;
    private readonly ILogger<RedisQuota> _logger;

    public RedisQuota(
        ICacheClient cache,
        IQuotaLimitStore store,
        ITenants tenants,
        QuotaFlushTracker tracker,
        QuotaOptions options,
        ILogger<RedisQuota> logger)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<QuotaDecision> ConsumeAsync(string meter, long amount, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(meter))
        {
            throw new ArgumentException("A meter id is required.", nameof(meter));
        }

        // A fresh key per attempt defeats the whole point: the retry arrives looking like new work.
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException(
                "An idempotency key is required, and it must be the domain object's own id.", nameof(idempotencyKey));
        }

        if (!_options.IsEnforced(meter))
        {
            return QuotaDecision.Skipped();
        }

        var tenantId = BlocksContext.GetContext()?.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId) || IsRootTenant(tenantId))
        {
            // The root tenant is skipped here rather than in the caller, so every path inherits it.
            return QuotaDecision.Skipped();
        }

        try
        {
            var decision = await EvaluateAsync(tenantId, meter, amount, idempotencyKey, cancellationToken).ConfigureAwait(false);

            if (decision.Outcome != QuotaOutcome.NotLoaded)
            {
                return decision;
            }

            // Cold key. Seed from the durable store and try once more; a second miss means the
            // meter genuinely has no limit row, which is not this call's problem to solve.
            if (!await SeedAsync(tenantId, meter, cancellationToken).ConfigureAwait(false))
            {
                return QuotaDecision.Skipped();
            }

            return await EvaluateAsync(tenantId, meter, amount, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or OperationCanceledException)
        {
            return OnUnreachable(meter, ex);
        }
    }

    public async Task<long> GetPolicyAsync(string meter, CancellationToken cancellationToken = default)
    {
        var tenantId = BlocksContext.GetContext()?.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return -1;
        }

        var row = await _store.GetAsync(tenantId, meter, cancellationToken).ConfigureAwait(false);
        return row?.Limit ?? -1;
    }

    private async Task<QuotaDecision> EvaluateAsync(string tenantId, string meter, long amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        var keys = new RedisKey[]
        {
            QuotaKeys.Counter(tenantId, meter),
            QuotaKeys.Seen(tenantId, idempotencyKey)
        };

        var values = new RedisValue[]
        {
            amount,
            (long)_options.IdempotencyWindow.TotalSeconds,
            (long)_options.CounterTtl.TotalSeconds
        };

        var result = await WithTimeoutAsync(
            _cache.CacheDatabase().ScriptEvaluateAsync(QuotaScript.ConsumeLua, keys, values),
            cancellationToken).ConfigureAwait(false);

        var parts = (RedisValue[])result!;
        var outcome = (QuotaOutcome)(int)parts[0];

        if (outcome == QuotaOutcome.Allowed)
        {
            // This process changed the counter, so this process knows what needs flushing.
            _tracker.Track(tenantId, meter);
        }

        return new QuotaDecision(outcome, (long)parts[1], (long)parts[2]);
    }

    private async Task<bool> SeedAsync(string tenantId, string meter, CancellationToken cancellationToken)
    {
        var row = await _store.GetAsync(tenantId, meter, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogWarning("No limit row for meter {Meter} on tenant {TenantId}; the call was not metered.", meter, tenantId);
            return false;
        }

        var keys = new RedisKey[] { QuotaKeys.Counter(tenantId, meter) };
        var values = new RedisValue[]
        {
            row.Value.Limit,
            row.Value.Used,
            row.Value.Purchased,
            (long)_options.CounterTtl.TotalSeconds
        };

        await WithTimeoutAsync(
            _cache.CacheDatabase().ScriptEvaluateAsync(QuotaScript.SeedLua, keys, values),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// A hard ceiling on the wait. Without it a slow counter does not merely break metering — it
    /// becomes the latency floor of every request on the platform.
    /// </summary>
    private async Task<RedisResult> WithTimeoutAsync(Task<RedisResult> call, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(_options.Timeout, timeout.Token);

        var finished = await Task.WhenAny(call, delay).ConfigureAwait(false);
        if (finished != call)
        {
            throw new TimeoutException($"The quota check did not answer within {_options.Timeout.TotalMilliseconds:0} ms.");
        }

        timeout.Cancel();
        return await call.ConfigureAwait(false);
    }

    private QuotaDecision OnUnreachable(string meter, Exception ex)
    {
        var mode = _options.FailModeFor(meter);

        if (mode == QuotaFailMode.Open)
        {
            _logger.LogError(ex, "Quota counter unreachable for {Meter}; failing open. Usage for this call is lost.", meter);
            return new QuotaDecision(QuotaOutcome.FailedOpen, -1, -1);
        }

        _logger.LogError(ex, "Quota counter unreachable for {Meter}; failing closed. Every unit of this meter is spend that cannot be recovered.", meter);
        return new QuotaDecision(QuotaOutcome.FailedClosed, -1, -1);
    }

    private bool IsRootTenant(string tenantId)
    {
        try
        {
            return _tenants.GetTenantByID(tenantId) is { IsRootTenant: true };
        }
        catch (Exception ex)
        {
            // Not knowing whether this is the platform's own traffic is not a reason to bill a
            // customer for it.
            _logger.LogWarning(ex, "Could not resolve tenant {TenantId} while metering; treating as root and skipping.", tenantId);
            return true;
        }
    }
}
