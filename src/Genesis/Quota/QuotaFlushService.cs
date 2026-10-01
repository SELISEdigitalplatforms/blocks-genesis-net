using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis;

/// <summary>
/// Carries counts from Redis, which is fast and not durable, to Mongo, which is durable and not
/// fast. Redis stays the operational record; this is what survives a restart.
/// </summary>
public sealed class QuotaFlushService : BackgroundService
{
    private readonly ICacheClient _cache;
    private readonly IQuotaLimitStore _store;
    private readonly QuotaFlushTracker _tracker;
    private readonly QuotaOptions _options;
    private readonly ILogger<QuotaFlushService> _logger;

    public QuotaFlushService(
        ICacheClient cache,
        IQuotaLimitStore store,
        QuotaFlushTracker tracker,
        QuotaOptions options,
        ILogger<QuotaFlushService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.FlushInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await FlushOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed flush costs at most one interval of counts, and the next tick picks the
                // work up again. It is never a reason to take the process down.
                _logger.LogError(ex, "Quota flush failed; up to one interval of counts may be lost.");
            }
        }

        // On the way out, take one last pass so a graceful shutdown does not throw away the
        // interval that was still in flight.
        await FlushOnceAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Carries everything tracked since the last pass. Public so a shutdown hook or an operator can
    /// force a flush without waiting for the next tick.
    /// </summary>
    public async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        var byTenant = _tracker.Drain();
        if (byTenant.Count == 0)
        {
            return;
        }

        var database = _cache.CacheDatabase();

        foreach (var (tenantId, meters) in byTenant)
        {
            var usage = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var meter in meters)
            {
                var value = await database.HashGetAsync(QuotaKeys.Counter(tenantId, meter), "used").ConfigureAwait(false);
                if (value.HasValue && value.TryParse(out long used))
                {
                    usage[meter] = used;
                }
            }

            if (usage.Count > 0)
            {
                // One write per tenant, not one per meter.
                await _store.FlushUsageAsync(tenantId, usage, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
