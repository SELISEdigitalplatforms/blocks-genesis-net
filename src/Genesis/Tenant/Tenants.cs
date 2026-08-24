using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;

namespace Blocks.Genesis;

public sealed class Tenants : ITenants, IDisposable
{
    public const string TenantCacheUpdateActionUpsert = "upsert";
    public const string TenantCacheUpdateActionRemove = "remove";

    private readonly ILogger<Tenants> _logger;
    private readonly IBlocksSecret _blocksSecret;
    private readonly ICacheClient _cacheClient;
    private readonly IMongoDatabase _database;
    private readonly string _tenantUpdateChannel = "tenant::updates";
    private bool _isSubscribed = false;
    private bool _disposed = false;

    private readonly ConcurrentDictionary<string, Tenant> _tenantCache = [];
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
    {
        _logger = logger;
        _blocksSecret = blocksSecret;
        _cacheClient = cacheClient;

        _database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RootDatabaseName);

        // Subscribe to tenant updates. The async method reports its own
        // failures internally and never throws synchronously.
        SubscribeToTenantUpdates().ConfigureAwait(true);
    }

    public Tenant? GetTenantByID(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;

        // Try to get tenant from the in-memory cache
        if (_tenantCache.TryGetValue(tenantId, out var tenant))
            return tenant;

        // Deduplicate concurrent DB lookups for the same tenant ID.
        var loader = _tenantLoadInProgress.GetOrAdd(
            tenantId,
            id => new Lazy<Tenant?>(() => GetTenantFromDb(id), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            tenant = loader.Value;
        }
        finally
        {
            _tenantLoadInProgress.TryRemove(tenantId, out _);
        }

        if (tenant != null)
        {
            _tenantCache[tenant.TenantId] = tenant;
            // Ensure trace collection exists asynchronously without blocking
            _ = EnsureTraceCollectionExistsAsync(tenant);
        }

        return tenant;
    }

    public Dictionary<string, (string, string)> GetTenantDatabaseConnectionStrings()
    {
        return _tenantCache.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.DBName, kvp.Value.DbConnectionString));
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
        return TryRevalidateTenant(tenantId)?.JwtTokenParameters ?? parameters;
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

        var refreshed = GetTenantFromDb(tenantId);
        if (refreshed is null)
        {
            return null;
        }

        _tenantCache[refreshed.TenantId] = refreshed;

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
            _tenantCache[tenant.TenantId] = tenant;

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




    private Tenant? GetTenantFromDb(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;

        try
        {
            return _database
                .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
                .Find(t => t.TenantId == tenantId && !t.IsDisabled)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve tenant from DB for ID: {TenantId}", tenantId);
            return null;
        }
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

        var cachedTenant = _tenantCache.Values.FirstOrDefault(tenant => tenant.Applications.Any(a => string.Equals(BlocksContext.NormalizeDomain(a.Domain), appName, StringComparison.OrdinalIgnoreCase)));

        if (cachedTenant != null)
        {
            return cachedTenant;
        }

        try
        {
            var builder = Builders<Tenant>.Filter;
            var domains = new List<string> {
              "http://" + appName,
              "https://" + appName,
            };

            var filter = builder.ElemMatch(x => x.Applications,app => domains.Contains(app.Domain));

            var tenant = _database
                .GetCollection<Tenant>(BlocksConstants.TenantCollectionName)
                .Find(filter)
                .FirstOrDefault();

            if (tenant != null)
            {
                _tenantCache[tenant.TenantId] = tenant;
                // Ensure trace collection exists asynchronously without blocking
                _ = EnsureTraceCollectionExistsAsync(tenant);
            }

            return tenant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve tenant from DB for Application name: {AppName}", appName);
            return null;
        }
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
