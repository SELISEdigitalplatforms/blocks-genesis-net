namespace Blocks.Genesis;

/// <summary>
/// Shared contract values for the Blocks Event Trigger. Genesis forwards catalogued events to
/// <see cref="QueueName"/>; blocks-logic consumes that queue, owns the catalog stored under
/// <see cref="CatalogKey"/> and publishes the tenant ID on <see cref="InvalidationChannel"/> after each write.
/// </summary>
public static class BlocksEventTriggerConstants
{
    /// <summary>The queue every forwarded <see cref="BlocksEventTriggerMessage"/> is sent to.</summary>
    public const string QueueName = "blocks_logic_workflow_blocks_event_trigger_listener";

    /// <summary>Single-value key of the catalog document in each tenant DB's <c>KeyValueStores</c>.</summary>
    public const string CatalogKey = "blocks-event-triggers";

    /// <summary>Redis channel carrying a plain tenant ID whose cached catalog must be dropped.</summary>
    public const string InvalidationChannel = "blocks-event-triggers::updates";

    /// <summary>Upper bound, in UTF-8 bytes, of a serialized forward. Larger forwards are dropped.</summary>
    public const int MaxForwardBytes = 196_608;

    /// <summary>Most tenants one process caches before the cache is cleared.</summary>
    public const int MaxCachedTenants = 10_000;

    /// <summary>How long a successfully read catalog (or a missing one) is cached.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long an empty result is cached after a failed catalog read.</summary>
    public static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromSeconds(30);
}
