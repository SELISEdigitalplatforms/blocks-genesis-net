using MongoDB.Driver;
using Serilog;
using System.Collections.Concurrent;

namespace Blocks.Genesis;

/// <summary>
/// Supplies a tenant's active external identity providers to the authentication path.
/// </summary>
public interface IThirdPartyJwtProviderStore
{
    /// <summary>
    /// Active providers for a tenant, newest read cached briefly. Never throws: an unreachable
    /// store yields an empty list, which rejects the request rather than failing it open.
    /// </summary>
    Task<IReadOnlyList<ThirdPartyJwtProvider>> GetActiveAsync(string tenantId);

    /// <summary>Drops a tenant's cached providers, so the next read reloads.</summary>
    void Invalidate(string tenantId);
}

/// <summary>
/// Reads <c>JwtThirdPartyProviders</c> from the root database, beside <c>Tenants</c>.
/// </summary>
/// <remarks>
/// <para>
/// Provider configuration used to ride along with the cached <see cref="Tenant"/>, which cost
/// nothing on the authentication hot path. Its own collection means a database read per
/// authentication unless cached, so this caches per tenant with a short lifetime — the same
/// bounded-staleness trade the tenant cache already makes for token parameters.
/// </para>
/// <para>
/// Concurrent misses for one tenant are deduplicated, so a burst of requests against a cold cache
/// produces one query rather than one per request.
/// </para>
/// </remarks>
public sealed class ThirdPartyJwtProviderStore : IThirdPartyJwtProviderStore
{
    internal const string CollectionName = "JwtThirdPartyProviders";

    /// <summary>
    /// How long a tenant's providers are served from memory. Deliberately short: a configuration
    /// change should take effect without a deploy, and the read is cheap.
    /// </summary>
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);

    private readonly IMongoDatabase _database;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<ThirdPartyJwtProvider>>>> _loads =
        new(StringComparer.Ordinal);

    public ThirdPartyJwtProviderStore(IBlocksSecret blocksSecret)
    {
        ArgumentNullException.ThrowIfNull(blocksSecret);

        _database = new MongoClient(blocksSecret.DatabaseConnectionString)
            .GetDatabase(blocksSecret.RootDatabaseName);
    }

    // Seam for tests: the database is otherwise reached only through the connection string.
    internal ThirdPartyJwtProviderStore(IMongoDatabase database)
    {
        _database = database;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThirdPartyJwtProvider>> GetActiveAsync(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return [];
        }

        if (_cache.TryGetValue(tenantId, out var cached) && !cached.IsStale)
        {
            return cached.Providers;
        }

        var loader = _loads.GetOrAdd(
            tenantId,
            id => new Lazy<Task<IReadOnlyList<ThirdPartyJwtProvider>>>(() => LoadAsync(id)));

        try
        {
            var providers = await loader.Value.ConfigureAwait(false);
            _cache[tenantId] = new CacheEntry(providers, DateTime.UtcNow);
            return providers;
        }
        finally
        {
            _loads.TryRemove(tenantId, out _);
        }
    }

    /// <inheritdoc />
    public void Invalidate(string tenantId)
    {
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            _cache.TryRemove(tenantId, out _);
        }
    }

    private async Task<IReadOnlyList<ThirdPartyJwtProvider>> LoadAsync(string tenantId)
    {
        try
        {
            var filter = Builders<ThirdPartyJwtProvider>.Filter.And(
                Builders<ThirdPartyJwtProvider>.Filter.Eq(p => p.TenantId, tenantId),
                Builders<ThirdPartyJwtProvider>.Filter.Eq(p => p.IsActive, true));

            var providers = await _database
                .GetCollection<ThirdPartyJwtProvider>(CollectionName)
                .Find(filter)
                .ToListAsync()
                .ConfigureAwait(false);

            return providers;
        }
        catch (Exception ex)
        {
            // An empty list rejects the request. Returning nothing is the safe answer here:
            // the alternative is falling through to some other validator on a store outage.
            Log.Error(ex, "[ThirdParty] Failed to load providers for tenant {TenantId}.", tenantId);
            return [];
        }
    }

    private sealed record CacheEntry(IReadOnlyList<ThirdPartyJwtProvider> Providers, DateTime LoadedUtc)
    {
        public bool IsStale => DateTime.UtcNow - LoadedUtc > CacheLifetime;
    }
}
