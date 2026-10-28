using System.Collections.Concurrent;
using Blocks.Genesis.Entities.Os;

namespace Blocks.Genesis.Notifications;

/// <summary>Reads the notification configuration a send uses.</summary>
internal interface INotificationConfigReader
{
    /// <summary>
    /// The tenant's KV <c>notification-config:&lt;name&gt;</c>, else the root tenant's row when it is a built-in.
    /// Null when neither exists. Must run with <see cref="BlocksContext"/> set to <paramref name="tenantId"/>.
    /// </summary>
    Task<NotificationConfiguration?> GetAsync(string tenantId, string name, CancellationToken cancellationToken);
}

/// <summary>Key-value store reads, cached 60 s per tenant and name (misses too, so unknown names cost no DB call).</summary>
internal sealed class NotificationConfigReader : INotificationConfigReader
{
    internal const string KeyPrefix = "notification-config:";
    internal static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(60);
    private const int MaxCachedEntries = 10_000;

    private readonly IKeyValueStore _keyValueStore;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(string TenantId, string Name), CachedConfig> _cache = new();

    public NotificationConfigReader(IKeyValueStore keyValueStore, TimeProvider timeProvider)
    {
        _keyValueStore = keyValueStore;
        _timeProvider = timeProvider;
    }

    public async Task<NotificationConfiguration?> GetAsync(string tenantId, string name, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue((tenantId, name), out var cached) && cached.ExpiresAt > now)
        {
            return cached.Configuration;
        }

        var configuration = await LoadAsync(name, cancellationToken);
        if (_cache.Count >= MaxCachedEntries)
        {
            _cache.Clear();
        }

        _cache[(tenantId, name)] = new CachedConfig(configuration, now + CacheTime);
        return configuration;
    }

    private async Task<NotificationConfiguration?> LoadAsync(string name, CancellationToken cancellationToken)
    {
        var key = KeyPrefix + name;
        var own = await _keyValueStore.GetAsync<NotificationConfiguration>(key, impersonated: true, cancellationToken);
        if (own is not null)
        {
            return own;
        }

        // Console built-ins are kept in root only; a root custom config never applies to another tenant.
        var root = await _keyValueStore.GetAsync<NotificationConfiguration>(key, impersonated: false, cancellationToken);
        return root is { IsBuiltIn: true } ? root : null;
    }

    private sealed record CachedConfig(NotificationConfiguration? Configuration, DateTimeOffset ExpiresAt);
}
