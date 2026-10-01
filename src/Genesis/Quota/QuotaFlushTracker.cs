using System.Collections.Concurrent;

namespace Blocks.Genesis;

/// <summary>
/// Which counters this process has touched since the last flush.
/// <para>
/// The process that incremented knows what it changed, so there is no need to scan Redis for keys.
/// Two instances tracking the same counter is harmless: the flush writes with <c>$max</c>, which is
/// idempotent and order-independent, so any process may flush and none of them need to agree.
/// </para>
/// </summary>
public sealed class QuotaFlushTracker
{
    private ConcurrentDictionary<(string TenantId, string Meter), byte> _touched = new();

    public void Track(string tenantId, string meter) => _touched[(tenantId, meter)] = 0;

    public int PendingCount => _touched.Count;

    /// <summary>
    /// Takes everything tracked so far and starts a fresh set, grouped by tenant so the flush can
    /// write once per tenant rather than once per meter.
    /// </summary>
    public IReadOnlyDictionary<string, List<string>> Drain()
    {
        var taken = Interlocked.Exchange(ref _touched, new ConcurrentDictionary<(string, string), byte>());

        var byTenant = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (tenantId, meter) in taken.Keys)
        {
            if (!byTenant.TryGetValue(tenantId, out var meters))
            {
                meters = [];
                byTenant[tenantId] = meters;
            }

            meters.Add(meter);
        }

        return byTenant;
    }
}
