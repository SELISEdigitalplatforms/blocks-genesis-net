using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading;

namespace Blocks.Genesis;

public sealed class Tenants : ITenants, IDisposable
{
    public const string TenantCacheUpdateActionUpsert = "upsert";
    public const string TenantCacheUpdateActionRemove = "remove";

    /// <summary>
    /// Environment variable that sets how long a cached tenant is served before it is read
    /// from the database again, in whole seconds. Missing or invalid values use
    /// <see cref="DefaultCacheLifetime"/>.
    /// </summary>
    public const string CacheLifetimeEnvironmentVariable = "TenantCacheLifetimeSeconds";

    /// <summary>Default lifetime of a cached tenant.</summary>
    public static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromMinutes(5);

    // After a failed reload the last good copy is served for this long (or the cache
    // lifetime, if shorter) before the next attempt, so a database outage does not turn
    // every request into a slow, failing read.
    private static readonly TimeSpan ReloadFailureRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger<Tenants> _logger;
    private readonly IBlocksSecret _blocksSecret;
    private readonly ICacheClient _cacheClient;
    private readonly IMongoDatabase _database;
    private readonly string _tenantUpdateChannel = "tenant::updates";
    private bool _isSubscribed = false;
    private bool _disposed = false;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheLifetime;

    // Change events keep the cache current; the lifetime on every entry is the backstop
    // for events that never arrive, so a disabled or changed tenant is picked up within
    // one lifetime at the latest.
    private readonly ConcurrentDictionary<string, TenantCacheEntry> _tenantCache = [];
    private readonly ConcurrentDictionary<string, Lazy<Tenant?>> _tenantLoadInProgress = [];
    private readonly ConcurrentDictionary<string, DateTime> _lastTokenParameterRevalidationUtc = [];

    // A cached tenant is only ever refreshed by a "tenant::updates" message, and Redis
    // pub/sub delivers at most once — a pod that is restarting, disconnected, or simply
    // started after the publish keeps the stale copy for its whole lifetime. When the
    // stale copy predates certificate provisioning it carries no PublicCertificatePath,
    // and token validation then fails for that tenant until the pod is restarted.
    // Re-reading the database on that specific miss bounds the damage to this interval.
    private static readonly TimeSpan TokenParameterRevalidationInterval = TimeSpan.FromMinutes(1);

    public Tenants(ILogger<Tenants> logger, IBlocksSecret blocksSecret, ICacheClient cacheClient)
        : this(logger, blocksSecret, cacheClient, TimeProvider.System, Environment.GetEnvironmentVariable)
    {
    }

    internal Tenants(
        ILogger<Tenants> logger,
        IBlocksSecret blocksSecret,
        ICacheClient cacheClient,
        TimeProvider timeProvider,
        Func<string, string?> readEnvironment)
    {
        _logger = logger;
        _blocksSecret = blocksSecret;
        _cacheClient = cacheClient;
        _timeProvider = timeProvider;
        _cacheLifetime = ResolveCacheLifetime(readEnvironment(CacheLifetimeEnvironmentVariable), logger);

        _database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RootDatabaseName);

        // Subscribe to tenant updates. The async method reports its own
        // failures internally and never throws synchronously.
        SubscribeToTenantUpdates().ConfigureAwait(true);
    }

    public Tenant? GetTenantByID(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;

        if (_tenantCache.TryGetValue(tenantId, out var entry))
        {
            if (entry.Tenant.IsDisabled)
            {
                // Never serve a disabled tenant; drop it and fall through to the database.
                _tenantCache.TryRemove(tenantId, out _);
            }
            else if (IsFresh(entry))
            {
                return entry.Tenant;
            }
        }

        return LoadTenantOnce(tenantId);
    }

    /// <summary>
    /// Loads a tenant from the database with at most one read in flight per tenant;
    /// concurrent callers for the same tenant wait for that read and share its result.
    /// </summary>
    private Tenant? LoadTenantOnce(string tenantId)
    {
        var loader = _tenantLoadInProgress.GetOrAdd(
            tenantId,
            id => new Lazy<Tenant?>(() => LoadAndCacheTenant(id), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return loader.Value;
        }
        finally
        {
            // Remove only this loader, never one a later caller has started since.
            _tenantLoadInProgress.TryRemove(new KeyValuePair<string, Lazy<Tenant?>>(tenantId, loader));
        }
    }

    private Tenant? LoadAndCacheTenant(string tenantId)
    {
        // A caller that arrives just after another load finished finds its result here
        // and does not read the database a second time.
        _tenantCache.TryGetValue(tenantId, out var previous);
        if (previous is { Tenant.IsDisabled: false } && IsFresh(previous))
        {
            return previous.Tenant;
        }

        Tenant? loaded;
        try
        {
            loaded = ReadTenantFromDb(tenantId);
        }
        catch (Exception ex)
        {
            return ServeLastGoodAfterFailedLoad(tenantId, previous, ex);
        }

        if (loaded is null)
        {
            // The database answered: the tenant is disabled or gone.
            _tenantCache.TryRemove(tenantId, out _);
            return null;
        }

        if (TryCacheTenant(loaded) && previous is null)
        {
            // Ensure trace collection exists asynchronously without blocking
            _ = EnsureTraceCollectionExistsAsync(loaded);
        }

        return loaded;
    }

    /// <summary>
    /// A failed read says nothing about the tenant. The last good copy (never a disabled
    /// one) is served and retried after a short delay; with no such copy the lookup fails
    /// like an unknown tenant.
    /// </summary>
    private Tenant? ServeLastGoodAfterFailedLoad(string tenantId, TenantCacheEntry? previous, Exception ex)
    {
        if (previous is null || previous.Tenant.IsDisabled)
        {
            _logger.LogError(ex, "Failed to retrieve tenant from DB for ID: {TenantId}", tenantId);
            return null;
        }

        var retryDelay = ReloadFailureRetryDelay < _cacheLifetime ? ReloadFailureRetryDelay : _cacheLifetime;
        _tenantCache.TryUpdate(tenantId, previous with { ExpiresAtUtc = _timeProvider.GetUtcNow() + retryDelay }, previous);

        _logger.LogWarning(
            ex,
            "Failed to reload tenant from DB; serving the last good cached copy and retrying in {RetryDelay}. TenantId: {TenantId}",
            retryDelay,
            tenantId);

        return previous.Tenant;
    }

    private bool IsFresh(TenantCacheEntry entry) => entry.ExpiresAtUtc > _timeProvider.GetUtcNow();

    private static TimeSpan ResolveCacheLifetime(string? configuredSeconds, ILogger logger)
    {
        if (configuredSeconds is null)
        {
            return DefaultCacheLifetime;
        }

        if (int.TryParse(configuredSeconds.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        logger.LogWarning(
            "Invalid {Variable} value; using the default tenant cache lifetime of {Lifetime}.",
            CacheLifetimeEnvironmentVariable,
            DefaultCacheLifetime);

        return DefaultCacheLifetime;
    }

    public Dictionary<string, (string, string)> GetTenantDatabaseConnectionStrings()
    {
        return _tenantCache.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.Tenant.DBName, kvp.Value.Tenant.DbConnectionString));
    }

    public (string?, string?) GetTenantDatabaseConnectionString(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return (null, null);

        var tenant = GetTenantByID(tenantId);
        return tenant is null ? (null, null) : (tenant.DBName, tenant.DbConnectionString);
    }

    public JwtTokenParameters? GetTenantTokenValidationParameter(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;

        var tenant = GetTenantByID(tenantId);
        var parameters = tenant?.JwtTokenParameters;

        // The tenant is genuinely unknown, or the cached copy is usable — nothing to do.
        // GetTenantByID has already consulted the database when the tenant was absent.
        if (tenant is null || !string.IsNullOrWhiteSpace(parameters?.PublicCertificatePath))
        {
            return parameters;
        }

        // A tenant with no certificate path cannot validate tokens, so the caller is about
        // to fail anyway. Spend one database read to rule out a stale cache entry before
        // that happens, rate limited so a tenant that is genuinely unconfigured does not
        // read the database on every request.
        var refreshed = TryRevalidateTenant(tenantId);
        if (refreshed is not null)
        {
            return refreshed.JwtTokenParameters;
        }

        // The re-read may have found the tenant disabled or deleted and evicted it.
        return _tenantCache.ContainsKey(tenantId) ? parameters : null;
    }

    /// <summary>
    /// Re-reads a single tenant from the database and refreshes the in-memory cache,
    /// at most once per <see cref="TokenParameterRevalidationInterval"/> per tenant.
    /// Returns the refreshed tenant, or null when throttled or when the read fails.
    /// </summary>
    private Tenant? TryRevalidateTenant(string tenantId)
    {
        var now = DateTime.UtcNow;
        var lastAttempt = _lastTokenParameterRevalidationUtc.GetOrAdd(tenantId, DateTime.MinValue);

        if (now - lastAttempt < TokenParameterRevalidationInterval)
        {
            return null;
        }

        // Losing this race means another thread is already revalidating; fall back to the
        // cached value rather than piling concurrent reads onto the same tenant.
        if (!_lastTokenParameterRevalidationUtc.TryUpdate(tenantId, now, lastAttempt))
        {
            return null;
        }

        Tenant? refreshed;
        try
        {
            refreshed = ReadTenantFromDb(tenantId);
        }
        catch (Exception ex)
        {
            // A failed read says nothing about the tenant; keep the cached copy.
            _logger.LogError(ex, "Failed to re-read tenant from DB for ID: {TenantId}", tenantId);
            return null;
        }

        if (refreshed is null)
        {
            // The database answered: the tenant is disabled or gone. Stop serving it.
            _tenantCache.TryRemove(tenantId, out _);
            _logger.LogInformation(
                "Tenant evicted from cache on re-read because it is disabled or missing. TenantId: {TenantId}",
                tenantId);
            return null;
        }

        TryCacheTenant(refreshed);

        var pathNowPresent = !string.IsNullOrWhiteSpace(refreshed.JwtTokenParameters?.PublicCertificatePath);

        _logger.LogInformation(
            "Tenant re-read from database because no public certificate path was cached. TenantId: {TenantId}, PathNowPresent: {PathNowPresent}",
            tenantId,
            pathNowPresent);

        return refreshed;
    }

    public Task UpdateTenantVersionAsync(TenantCacheUpdateMessage cacheUpdate)
    {
        if (cacheUpdate is null)
        {
            throw new ArgumentNullException(nameof(cacheUpdate));
        }

        return UpdateTenantVersionInternalAsync(cacheUpdate);
    }

    private async Task UpdateTenantVersionInternalAsync(TenantCacheUpdateMessage cacheUpdate)
    {
        try
        {
            var normalizedUpdate = NormalizeCacheUpdate(cacheUpdate);
            if (normalizedUpdate is null)
            {
                _logger.LogWarning("Skipping invalid tenant cache update payload.");
                return;
            }

            // Publish the update to notify all instances
            await _cacheClient.PublishAsync(_tenantUpdateChannel, JsonSerializer.Serialize(normalizedUpdate));

            _logger.LogInformation(
                "Tenant cache update published. TenantId: {TenantId}, Action: {Action}",
                ResolveTenantId(normalizedUpdate),
                normalizedUpdate.Action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update tenant version.");
        }
    }

    private async Task SubscribeToTenantUpdates()
    {
        if (_isSubscribed) return;

        try
        {
            await _cacheClient.SubscribeAsync(_tenantUpdateChannel, HandleTenantUpdate);
            _isSubscribed = true;
            _logger.LogInformation("Successfully subscribed to tenant updates channel.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to tenant updates channel.");
        }
    }

    private void HandleTenantUpdate(RedisChannel channel, RedisValue message)
    {
        try
        {
            var cacheUpdate = ParseTenantCacheUpdate(message.ToString());
            if (cacheUpdate is null)
            {
                _logger.LogWarning("Received invalid tenant update payload.");
                return;
            }

            cacheUpdate = NormalizeCacheUpdate(cacheUpdate);
            if (cacheUpdate is null)
            {
                _logger.LogWarning("Skipping tenant update due to missing action/tenant details.");
                return;
            }

            var tenantId = ResolveTenantId(cacheUpdate);

            _logger.LogInformation(
                "Received tenant update notification. TenantId: {TenantId}, Action: {Action}",
                tenantId,
                cacheUpdate.Action);

            if (cacheUpdate.Action == TenantCacheUpdateActionRemove)
            {
                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    _tenantCache.TryRemove(tenantId, out _);
                }

                return;
            }

            // NormalizeCacheUpdate rejects upsert payloads without a tenant,
            // so the tenant is always present here.
            var tenant = cacheUpdate.Tenant!;

            if (tenant.IsDisabled)
            {
                _tenantCache.TryRemove(tenant.TenantId, out _);
                return;
            }

            bool isNewTenant = !_tenantCache.ContainsKey(tenant.TenantId);
            TryCacheTenant(tenant);

            if (isNewTenant)
            {
                // Ensure trace collection exists asynchronously without blocking the update handler
                _ = EnsureTraceCollectionExistsAsync(tenant);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling tenant update notification.");
        }
    }

    /// <summary>
    /// The only way a tenant enters the in-memory cache. A disabled tenant is never cached;
    /// any copy already held for it is removed instead.
    /// </summary>
    private bool TryCacheTenant(Tenant? tenant)
    {
        if (tenant is null || string.IsNullOrWhiteSpace(tenant.TenantId)) return false;

        if (tenant.IsDisabled)
        {
            _tenantCache.TryRemove(tenant.TenantId, out _);
            return false;
        }

        _tenantCache[tenant.TenantId] = new TenantCacheEntry(tenant, _timeProvider.GetUtcNow() + _cacheLifetime);
        return true;
    }

    /// <summary>
    /// Reads an enabled tenant. Returns null when it is missing or disabled; throws when the
    /// database cannot be read, so callers can tell "not there" from "could not ask".
    /// </summary>
    private Tenant? ReadTenantFromDb(string tenantId)
    {
        return _database
            .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
            .Find(t => t.TenantId == tenantId && !t.IsDisabled)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Unsubscribe from tenant updates
        if (_isSubscribed)
        {
            try
            {
                _cacheClient.UnsubscribeAsync(_tenantUpdateChannel).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unsubscribing from tenant updates channel.");
            }
        }

        _disposed = true;
    }

    public Tenant? GetTenantByApplicationDomain(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return null;

        appName = BlocksContext.NormalizeDomain(appName);

        var cachedTenant = _tenantCache.Values
            .Select(entry => entry.Tenant)
            .FirstOrDefault(tenant => !tenant.IsDisabled && HasApplicationDomain(tenant, appName));

        if (cachedTenant != null)
        {
            // GetTenantByID serves a fresh entry as is and reloads an expired one.
            var current = GetTenantByID(cachedTenant.TenantId);
            if (current != null && HasApplicationDomain(current, appName))
            {
                return current;
            }
        }

        try
        {
            var builder = Builders<Tenant>.Filter;
            var domains = new List<string> {
              "http://" + appName,
              "https://" + appName,
            };

            var filter = builder.And(
                builder.ElemMatch(x => x.Applications, app => domains.Contains(app.Domain)),
                builder.Where(x => !x.IsDisabled));

            var tenant = _database
                .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
                .Find(filter)
                .FirstOrDefault();

            if (TryCacheTenant(tenant))
            {
                // Ensure trace collection exists asynchronously without blocking
                _ = EnsureTraceCollectionExistsAsync(tenant!);
            }

            return tenant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve tenant from DB for Application name: {AppName}", appName);
            return null;
        }
    }


    private static bool HasApplicationDomain(Tenant tenant, string normalizedDomain)
    {
        return tenant.Applications.Any(a => string.Equals(BlocksContext.NormalizeDomain(a.Domain), normalizedDomain, StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureTraceCollectionExistsAsync(Tenant tenant)
    {
        if (tenant is null) return;

        // Only create trace collection for recently created tenants (< 24 hours old)
        if (tenant.CreatedDate <= DateTime.UtcNow.AddDays(-1))
            return;

        try
        {
            await Task.Run(() => LmtConfiguration.CreateCollectionForTrace(
                _blocksSecret.TraceConnectionString,
                tenant.TenantId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure trace collection for tenant: {TenantId}", tenant.TenantId);
        }
    }

    private static TenantCacheUpdateMessage? ParseTenantCacheUpdate(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            try
            {
                var cacheUpdate = JsonSerializer.Deserialize<TenantCacheUpdateMessage>(message);
                if (cacheUpdate != null)
                {
                    return cacheUpdate;
                }
            }
            catch (JsonException)
            {
                // Malformed cache payload is ignored; the caller falls back to null.
            }
        }

        return null;
    }

    private static TenantCacheUpdateMessage? NormalizeCacheUpdate(TenantCacheUpdateMessage cacheUpdate)
    {
        var action = (cacheUpdate.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action != TenantCacheUpdateActionRemove && action != TenantCacheUpdateActionUpsert)
        {
            return null;
        }

        if (action == TenantCacheUpdateActionRemove)
        {
            var tenantId = ResolveTenantId(cacheUpdate);
            return string.IsNullOrWhiteSpace(tenantId)
                ? null
                : cacheUpdate with { Action = TenantCacheUpdateActionRemove, TenantId = tenantId };
        }

        if (cacheUpdate.Tenant is null || string.IsNullOrWhiteSpace(cacheUpdate.Tenant.TenantId))
        {
            return null;
        }

        return cacheUpdate with { Action = TenantCacheUpdateActionUpsert, TenantId = cacheUpdate.Tenant.TenantId };
    }

    private static string? ResolveTenantId(TenantCacheUpdateMessage cacheUpdate)
    {
        return string.IsNullOrWhiteSpace(cacheUpdate.TenantId)
            ? cacheUpdate.Tenant?.TenantId
            : cacheUpdate.TenantId;
    }
}
