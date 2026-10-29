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

    /// <summary>
    /// Longest a miss waits for the catalog read. A miss is awaited on the send path, so a slow or unreachable
    /// tenant DB must not hold sends for the driver's own (30 s) timeout. A timed-out read is a failed read.
    /// </summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Longest a subscribe attempt may stay pending before it counts as failed and may be retried.</summary>
    internal static readonly TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(5);

    private readonly IKeyValueStore _keyValueStore;
    private readonly ICacheClient _cacheClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BlocksEventCatalogCache> _logger;

    private readonly ConcurrentDictionary<string, CachedCatalog> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<FrozenSet<string>>>> _inFlight = new(StringComparer.Ordinal);
    private readonly object _subscribeLock = new();

    // Per-tenant invalidation versions: a read stores its result only if its tenant's version did not move.
    // The epoch changes when the map is cleared, so a cleared version can never look unchanged.
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);
    private long _versionEpoch;
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

    /// <summary>Test seam: runs after a read's version check and before its result is stored.</summary>
    internal Action<string>? BeforeStore { get; set; }

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
        if (_versions.Count >= BlocksEventTriggerConstants.MaxCachedTenants && !_versions.ContainsKey(tenantId))
        {
            Interlocked.Increment(ref _versionEpoch);
            _versions.Clear();
        }

        // Version first, then the entry: a read that stored in between sees the new version and removes itself.
        _versions.AddOrUpdate(tenantId, 1, (_, version) => version + 1);
        _entries.TryRemove(tenantId, out _);
    }

    private (long Epoch, long Version) ReadVersion(string tenantId)
    {
        var epoch = Interlocked.Read(ref _versionEpoch);
        return (epoch, _versions.TryGetValue(tenantId, out var version) ? version : 0);
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
        var versionAtStart = ReadVersion(tenantId);
        // Lifetimes count from the read's start, so a slow read never extends how long a result is trusted.
        var startedAt = _timeProvider.GetUtcNow();
        FrozenSet<string> enabled;
        TimeSpan lifetime;

        try
        {
            var catalog = await ReadCatalogAsync().ConfigureAwait(false);

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

        StoreUnlessInvalidated(tenantId, versionAtStart, new CachedCatalog(enabled, startedAt + lifetime));
        return enabled;
    }

    // Starts the read synchronously on the caller's flow, so the impersonated read resolves the caller's tenant.
    private async Task<BsonArray?> ReadCatalogAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var read = _keyValueStore.GetAsync<BsonArray>(
            BlocksEventTriggerConstants.CatalogKey, impersonated: true, cancellation.Token);

        try
        {
            // WaitAsync bounds the wait even for a store that ignores the token.
            return await read.WaitAsync(ReadTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            // The abandoned read may still fault; observe it so it is not reported as unobserved.
            _ = read.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    // An invalidation that arrived while reading may describe a newer catalog than the one just read. Check,
    // store, then check again: an invalidation landing between the first check and the store removes it.
    private void StoreUnlessInvalidated(string tenantId, (long Epoch, long Version) versionAtStart, CachedCatalog entry)
    {
        if (ReadVersion(tenantId) != versionAtStart)
        {
            return;
        }

        BeforeStore?.Invoke(tenantId);
        Store(tenantId, entry);

        if (ReadVersion(tenantId) != versionAtStart)
        {
            _entries.TryRemove(new KeyValuePair<string, CachedCatalog>(tenantId, entry));
        }
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
        Task? attempt = null;
        Exception? failure = null;
        try
        {
            attempt = _cacheClient.SubscribeAsync(BlocksEventTriggerConstants.InvalidationChannel, OnInvalidation);
            // Bounded, so a subscribe that never completes still clears the flag and C11's retry still happens.
            await attempt.WaitAsync(SubscribeTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        lock (_subscribeLock)
        {
            // Never back to false: a late success of an earlier, timed-out attempt may already have set it.
            _subscribed |= failure is null;
            _subscribing = false;
        }

        if (failure is null)
        {
            return;
        }

        if (failure is TimeoutException && attempt is not null)
        {
            // A late success still delivers invalidations, so record it rather than subscribing twice.
            _ = attempt.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        lock (_subscribeLock)
                        {
                            _subscribed = true;
                        }
                    }
                    else
                    {
                        _ = t.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        LogSubscribeFailure(failure);
    }

    private void LogSubscribeFailure(Exception ex) =>
        _logger.LogWarning(
            "Blocks event trigger invalidation subscribe failed ({ErrorType}); cached catalogs expire after {Minutes} min",
            ex.GetType().Name, BlocksEventTriggerConstants.CacheLifetime.TotalMinutes);

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
