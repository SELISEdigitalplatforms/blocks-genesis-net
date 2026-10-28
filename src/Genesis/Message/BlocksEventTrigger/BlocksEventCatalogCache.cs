using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Blocks.Genesis;

/// <summary>Per-tenant set of event names enabled for workflows.</summary>
internal interface IBlocksEventCatalogCache
{
    /// <summary>
    /// The enabled set for <paramref name="tenantId"/>. Completes synchronously on a cache hit. Must run with
    /// <see cref="BlocksContext"/> set to that tenant, because a miss reads the tenant's own DB.
    /// </summary>
    ValueTask<FrozenSet<string>> GetEnabledEventsAsync(string tenantId);
}

/// <summary>
/// Caches each tenant's enabled events for <see cref="BlocksEventTriggerConstants.CacheLifetime"/>, with one
/// catalog read per tenant at a time, Redis invalidation and a failure cache. Never throws to the caller.
/// </summary>
internal sealed class BlocksEventCatalogCache : IBlocksEventCatalogCache
{
    internal static readonly TimeSpan SubscribeRetryInterval = TimeSpan.FromMinutes(1);

    private readonly IKeyValueStore _keyValueStore;
    private readonly ICacheClient _cacheClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BlocksEventCatalogCache> _logger;

    private readonly ConcurrentDictionary<string, CachedCatalog> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<FrozenSet<string>>>> _inFlight = new(StringComparer.Ordinal);
    private readonly object _subscribeLock = new();

    private long _invalidations;
    private bool _subscribed;
    private bool _subscribing;
    private DateTimeOffset _nextSubscribeAttempt = DateTimeOffset.MinValue;

    public BlocksEventCatalogCache(
        IKeyValueStore keyValueStore,
        ICacheClient cacheClient,
        TimeProvider timeProvider,
        ILogger<BlocksEventCatalogCache> logger)
    {
        _keyValueStore = keyValueStore;
        _cacheClient = cacheClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal int CachedTenantCount => _entries.Count;

    public ValueTask<FrozenSet<string>> GetEnabledEventsAsync(string tenantId)
    {
        if (_entries.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return new ValueTask<FrozenSet<string>>(cached.Enabled);
        }

        return new ValueTask<FrozenSet<string>>(LoadSingleFlightAsync(tenantId));
    }

    /// <summary>Drops one tenant's entry. Called for each message on the invalidation channel.</summary>
    internal void Invalidate(string tenantId)
    {
        Interlocked.Increment(ref _invalidations);
        _entries.TryRemove(tenantId, out _);
    }

    private async Task<FrozenSet<string>> LoadSingleFlightAsync(string tenantId)
    {
        EnsureSubscribed();

        var load = _inFlight.GetOrAdd(tenantId, id => new Lazy<Task<FrozenSet<string>>>(() => LoadAsync(id)));
        try
        {
            return await load.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<FrozenSet<string>>>>(tenantId, load));
        }
    }

    private async Task<FrozenSet<string>> LoadAsync(string tenantId)
    {
        var invalidationsAtStart = Interlocked.Read(ref _invalidations);
        FrozenSet<string> enabled;
        TimeSpan lifetime;

        try
        {
            var catalog = await _keyValueStore
                .GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, impersonated: true)
                .ConfigureAwait(false);

            enabled = BuildEnabledSet(tenantId, catalog);
            lifetime = BlocksEventTriggerConstants.CacheLifetime;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Blocks event trigger catalog read failed for tenant {TenantId} ({ErrorType}); no events are forwarded for {Seconds} s",
                tenantId, ex.GetType().Name, BlocksEventTriggerConstants.FailureCacheLifetime.TotalSeconds);

            enabled = FrozenSet<string>.Empty;
            lifetime = BlocksEventTriggerConstants.FailureCacheLifetime;
        }

        // An invalidation that arrived while reading may describe a newer catalog than the one just read.
        if (Interlocked.Read(ref _invalidations) == invalidationsAtStart)
        {
            Store(tenantId, new CachedCatalog(enabled, _timeProvider.GetUtcNow() + lifetime));
        }

        return enabled;
    }

    private void Store(string tenantId, CachedCatalog entry)
    {
        if (_entries.Count >= BlocksEventTriggerConstants.MaxCachedTenants && !_entries.ContainsKey(tenantId))
        {
            _entries.Clear();
        }

        _entries[tenantId] = entry;
    }

    private FrozenSet<string> BuildEnabledSet(string tenantId, BsonArray? catalog)
    {
        if (catalog is null || catalog.Count == 0)
        {
            return FrozenSet<string>.Empty;
        }

        var enabled = new HashSet<string>(StringComparer.Ordinal);
        var malformed = 0;

        foreach (var element in catalog)
        {
            if (TryReadEntry(element, out var entry))
            {
                if (entry.IsValidForWorkflow)
                {
                    enabled.Add(entry.EventName);
                }
            }
            else
            {
                malformed++;
            }
        }

        // Once per read, so at most once per tenant per cache lifetime.
        if (malformed > 0)
        {
            _logger.LogWarning(
                "Blocks event trigger catalog for tenant {TenantId} has {MalformedCount} malformed entries; they are ignored",
                tenantId, malformed);
        }

        return enabled.ToFrozenSet(StringComparer.Ordinal);
    }

    private static bool TryReadEntry(BsonValue element, out BlocksEventCatalogEntry entry)
    {
        entry = null!;
        try
        {
            if (element is not BsonDocument document
                || !document.TryGetValue(nameof(BlocksEventCatalogEntry.EventName), out var eventName)
                || !eventName.IsString
                || string.IsNullOrWhiteSpace(eventName.AsString)
                || !IsBoolOrMissing(document, nameof(BlocksEventCatalogEntry.IsValidForWorkflow))
                || !IsBoolOrMissing(document, nameof(BlocksEventCatalogEntry.IsValidForFunction)))
            {
                return false;
            }

            entry = BsonSerializer.Deserialize<BlocksEventCatalogEntry>(document);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The default boolean serializer also accepts numbers and "true"/"false" strings; the catalog contract does not.
    private static bool IsBoolOrMissing(BsonDocument document, string name) =>
        !document.TryGetValue(name, out var value) || value.IsBoolean;

    private void EnsureSubscribed()
    {
        lock (_subscribeLock)
        {
            if (_subscribed || _subscribing || _timeProvider.GetUtcNow() < _nextSubscribeAttempt)
            {
                return;
            }

            _subscribing = true;
            _nextSubscribeAttempt = _timeProvider.GetUtcNow() + SubscribeRetryInterval;
        }

        // Not awaited: a send never waits on Redis.
        _ = SubscribeAsync();
    }

    private async Task SubscribeAsync()
    {
        var subscribed = false;
        try
        {
            await _cacheClient
                .SubscribeAsync(BlocksEventTriggerConstants.InvalidationChannel, OnInvalidation)
                .ConfigureAwait(false);
            subscribed = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Blocks event trigger invalidation subscribe failed ({ErrorType}); cached catalogs expire after {Minutes} min",
                ex.GetType().Name, BlocksEventTriggerConstants.CacheLifetime.TotalMinutes);
        }
        finally
        {
            lock (_subscribeLock)
            {
                _subscribed = subscribed;
                _subscribing = false;
            }
        }
    }

    private void OnInvalidation(RedisChannel channel, RedisValue message)
    {
        var tenantId = message.ToString();
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            Invalidate(tenantId);
        }
    }

    private sealed record CachedCatalog(FrozenSet<string> Enabled, DateTimeOffset ExpiresAt);
}
