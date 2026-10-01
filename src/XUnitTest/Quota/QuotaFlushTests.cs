using Blocks.Genesis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace XUnitTest.Quota;

public class QuotaFlushTrackerTests
{
    [Fact]
    public void Drain_GroupsByTenantSoTheFlushCanWriteOncePerTenant()
    {
        var tracker = new QuotaFlushTracker();
        tracker.Track("t1", "api.calls");
        tracker.Track("t1", "ai.agents");
        tracker.Track("t2", "api.calls");

        var byTenant = tracker.Drain();

        Assert.Equal(2, byTenant.Count);
        Assert.Equal(2, byTenant["t1"].Count);
        Assert.Single(byTenant["t2"]);
    }

    [Fact]
    public void TrackingTheSameCounterTwice_IsOneEntry()
    {
        var tracker = new QuotaFlushTracker();
        tracker.Track("t1", "api.calls");
        tracker.Track("t1", "api.calls");

        Assert.Equal(1, tracker.PendingCount);
    }

    [Fact]
    public void Drain_StartsAFreshSet()
    {
        var tracker = new QuotaFlushTracker();
        tracker.Track("t1", "api.calls");

        Assert.Single(tracker.Drain());
        Assert.Empty(tracker.Drain());
    }
}

public class QuotaFlushServiceTests
{
    private readonly Mock<ICacheClient> _cache = new();
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IQuotaLimitStore> _store = new();
    private readonly QuotaFlushTracker _tracker = new();
    private readonly QuotaOptions _options = new();

    public QuotaFlushServiceTests() => _cache.Setup(c => c.CacheDatabase()).Returns(_db.Object);

    private QuotaFlushService Subject() =>
        new(_cache.Object, _store.Object, _tracker, _options, NullLogger<QuotaFlushService>.Instance);

    [Fact]
    public async Task Flush_WritesOncePerTenant_NotOncePerMeter()
    {
        _tracker.Track("t1", "api.calls");
        _tracker.Track("t1", "ai.agents");
        _db.Setup(d => d.HashGetAsync(It.IsAny<RedisKey>(), "used", CommandFlags.None)).ReturnsAsync((RedisValue)7);

        IReadOnlyDictionary<string, long>? written = null;
        _store.Setup(s => s.FlushUsageAsync("t1", It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()))
              .Callback<string, IReadOnlyDictionary<string, long>, CancellationToken>((_, u, _) => written = u)
              .Returns(Task.CompletedTask);

        await Subject().FlushOnceAsync(CancellationToken.None);

        _store.Verify(s => s.FlushUsageAsync("t1", It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, written!.Count);
        Assert.Equal(7, written["api.calls"]);
    }

    [Fact]
    public async Task Flush_SkipsCountersRedisNoLongerHas()
    {
        _tracker.Track("t1", "api.calls");
        _db.Setup(d => d.HashGetAsync(It.IsAny<RedisKey>(), "used", CommandFlags.None)).ReturnsAsync(RedisValue.Null);

        await Subject().FlushOnceAsync(CancellationToken.None);

        _store.Verify(s => s.FlushUsageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Flush_DoesNothingWhenNothingWasTouched()
    {
        await Subject().FlushOnceAsync(CancellationToken.None);

        _cache.Verify(c => c.CacheDatabase(), Times.Never);
        _store.Verify(s => s.FlushUsageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Flush_TakesTheWorkSoASecondPassDoesNotRepeatIt()
    {
        _tracker.Track("t1", "api.calls");
        _db.Setup(d => d.HashGetAsync(It.IsAny<RedisKey>(), "used", CommandFlags.None)).ReturnsAsync((RedisValue)3);
        _store.Setup(s => s.FlushUsageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        await Subject().FlushOnceAsync(CancellationToken.None);
        await Subject().FlushOnceAsync(CancellationToken.None);

        _store.Verify(s => s.FlushUsageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, long>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
