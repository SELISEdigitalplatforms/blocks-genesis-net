namespace Blocks.Genesis;

/// <summary>
/// Key shapes. The tenant is in a hash tag so a Redis Cluster shards across tenants while one
/// tenant's keys stay together. A single tenant's meter lives on one slot, which is fine: the
/// quota runs out long before the key does — prod's included 1.2M calls is 0.46 per second across
/// a 30-day period.
/// <para>
/// The period is deliberately NOT in the key. blocks-os reseeds at the boundary and the TTL is
/// only a backstop, so Genesis never has to know when a tenant's period turns over.
/// </para>
/// </summary>
internal static class QuotaKeys
{
    public static string Counter(string tenantId, string meter) => $"q:{{{tenantId}}}:{meter}";

    public static string Seen(string tenantId, string idempotencyKey) =>
        $"q:{{{tenantId}}}:seen:{idempotencyKey}";
}
