using Blocks.Genesis;
using Blocks.Genesis.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace XUnitTest.Notifications;

public sealed class RedisNotificationRateLimiterTests
{
    private readonly Mock<IDatabase> _redis = new();

    private RedisNotificationRateLimiter Limiter()
    {
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.CacheDatabase()).Returns(_redis.Object);
        return new RedisNotificationRateLimiter(cache.Object, new ManualTime(), NullLogger<RedisNotificationRateLimiter>.Instance);
    }

    [Fact]
    public async Task CountsPerTenantConfigAndMinute_RefusesOverTheLimit()
    {
        RedisKey? key = null;
        _redis.Setup(r => r.StringIncrementAsync(It.IsAny<RedisKey>(), 1, CommandFlags.None))
            .Callback((RedisKey k, long _, CommandFlags _) => key = k)
            .ReturnsAsync(3);
        var limiter = Limiter();

        Assert.False(await limiter.TryAcquireAsync("T1", "orders-paid", 2));
        Assert.Equal("blocks:notification:rate:T1:orders-paid:202610281200", key.ToString());
        Assert.True(await limiter.TryAcquireAsync("T1", "orders-paid", 3));
    }

    [Fact]
    public async Task RedisDown_SendsAnyway()
    {
        _redis.Setup(r => r.StringIncrementAsync(It.IsAny<RedisKey>(), 1, CommandFlags.None))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        Assert.True(await Limiter().TryAcquireAsync("T1", "orders-paid", 1));
    }
}
