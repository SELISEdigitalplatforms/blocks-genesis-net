using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.Notifications;

/// <summary>Sends per tenant and configuration per minute.</summary>
internal interface INotificationRateLimiter
{
    /// <summary>Counts one send; false when the minute's count is over <paramref name="limitPerMinute"/>.</summary>
    Task<bool> TryAcquireAsync(string tenantId, string name, int limitPerMinute);
}

/// <summary>
/// A Redis counter per tenant, configuration and UTC minute, shared by every instance of every service. When Redis
/// fails the send goes ahead (fail open, logged): a lost notification costs more than a short burst.
/// </summary>
internal sealed class RedisNotificationRateLimiter : INotificationRateLimiter
{
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromMinutes(2);

    private readonly ICacheClient _cacheClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RedisNotificationRateLimiter> _logger;

    public RedisNotificationRateLimiter(ICacheClient cacheClient, TimeProvider timeProvider, ILogger<RedisNotificationRateLimiter> logger)
    {
        _cacheClient = cacheClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(string tenantId, string name, int limitPerMinute)
    {
        var minute = _timeProvider.GetUtcNow().ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
        var key = $"blocks:notification:rate:{tenantId}:{name}:{minute}";
        try
        {
            var database = _cacheClient.CacheDatabase();
            var count = await database.StringIncrementAsync(key);
            if (count == 1)
            {
                await database.KeyExpireAsync(key, KeyLifetime);
            }

            if (count <= limitPerMinute)
            {
                return true;
            }

            _logger.LogWarning("Notification {Name} over its rate limit of {Limit} per minute: dropped ({Count} this minute)",
                name, limitPerMinute, count);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Notification {Name} rate limit not checked, sent anyway: {Error}", name, ex.GetType().Name); // NOSONAR S6667 — type only
            return true;
        }
    }
}
