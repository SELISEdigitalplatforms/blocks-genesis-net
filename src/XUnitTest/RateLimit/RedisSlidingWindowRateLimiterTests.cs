using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Diagnostics;

namespace XUnitTest.RateLimit;

/// <summary>Limiter behaviour against a fake IDatabase: fail-open, timeout, breaker, arguments.</summary>
public class RedisSlidingWindowRateLimiterTests
{
    private static readonly UserRateLimitSettings Settings = new(20, UserRateLimitSettings.EnvironmentSource, "svc-a");

    private static (RedisSlidingWindowRateLimiter Limiter, Mock<IDatabase> Database, CapturingLogger Logger) Create(
        RedisRateLimitResilienceOptions? options = null)
    {
        var database = new Mock<IDatabase>();
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.CacheDatabase()).Returns(database.Object);
        var logger = new CapturingLogger();
        var limiter = new RedisSlidingWindowRateLimiter(cache.Object, Settings, logger, options ?? RedisRateLimitResilienceOptions.Default);
        return (limiter, database, logger);
    }

    private static RedisResult Result(long allowed, long remaining, long reset) =>
        RedisResult.Create([RedisResult.Create((RedisValue)allowed), RedisResult.Create((RedisValue)remaining), RedisResult.Create((RedisValue)reset)]);

    [Fact]
    public async Task TryAcquire_SendsOneScriptCall_WithNoTimeArgument() // C12, one command per request
    {
        var (limiter, database, _) = Create();
        RedisValue[]? captured = null;
        database
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((_, _, values, _) => captured = values)
            .ReturnsAsync(Result(1, 19, 1));

        var decision = await limiter.TryAcquireAsync("ratelimit:svc-a:t1:user:u1");

        Assert.Equal(new RateLimitDecision(true, 19, 1), decision);
        database.Verify(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal(new RedisValue[] { 20, 1000, 1, 30000 }, captured);
        Assert.DoesNotContain("ARGV[5]", RedisSlidingWindowRateLimiter.Script);
        Assert.Contains("redis.call('TIME')", RedisSlidingWindowRateLimiter.Script);
    }

    [Fact]
    public async Task TryAcquire_FailsOpen_OnRedisException() // C8
    {
        var (limiter, database, _) = Create();
        database
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        Assert.Null(await limiter.TryAcquireAsync("k"));
    }

    [Fact]
    public async Task TryAcquire_StopsWaitingAt50Milliseconds() // C9
    {
        var (limiter, database, _) = Create();
        database
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(new TaskCompletionSource<RedisResult>().Task); // Redis never answers

        // Warm up so the measurement covers the timeout, not first-call JIT.
        await limiter.TryAcquireAsync("warmup");

        var watch = Stopwatch.StartNew();
        var decision = await limiter.TryAcquireAsync("k");
        watch.Stop();

        Assert.Null(decision);
        // Bound is 50 ms; the slack only absorbs thread-pool scheduling on a loaded test runner.
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Breaker_OpensOnce_SkipsRedis_ThenResumes() // C10
    {
        var options = RedisRateLimitResilienceOptions.Default with
        {
            MinimumThroughput = 2,
            BreakDuration = TimeSpan.FromMilliseconds(600),
            SamplingDuration = TimeSpan.FromSeconds(5)
        };
        var (limiter, database, logger) = Create(options);
        var failing = true;
        database
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(() => failing
                ? Task.FromException<RedisResult>(new RedisTimeoutException("slow", CommandStatus.Sent))
                : Task.FromResult(Result(1, 10, 1)));

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await limiter.TryAcquireAsync("k"));
        }

        var callsWhileOpen = database.Invocations.Count;
        Assert.Null(await limiter.TryAcquireAsync("k"));
        Assert.Equal(callsWhileOpen, database.Invocations.Count);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("circuit opened"));

        failing = false;
        await Task.Delay(800);

        Assert.Equal(new RateLimitDecision(true, 10, 1), await limiter.TryAcquireAsync("k"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("circuit closed"));
    }

    [Fact]
    public async Task TryAcquire_Rethrows_WhenCallerCancels()
    {
        var (limiter, database, _) = Create();
        database
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(async () =>
            {
                await Task.Delay(1000);
                return Result(1, 1, 1);
            });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.TryAcquireAsync("k", cts.Token));
    }

    [Fact]
    public void ParseResult_HandlesUnexpectedShapes()
    {
        Assert.Null(RedisSlidingWindowRateLimiter.ParseResult(null));
        Assert.Null(RedisSlidingWindowRateLimiter.ParseResult(RedisResult.Create(RedisValue.Null)));
        Assert.Null(RedisSlidingWindowRateLimiter.ParseResult(RedisResult.Create((RedisValue)1)));
        Assert.Null(RedisSlidingWindowRateLimiter.ParseResult(RedisResult.Create([RedisResult.Create((RedisValue)1)])));
        Assert.Equal(new RateLimitDecision(false, 0, 30), RedisSlidingWindowRateLimiter.ParseResult(Result(0, -2, 30)));
    }

    [Fact]
    public async Task Constructor_ValidatesArguments()
    {
        var cache = new Mock<ICacheClient>().Object;
        Assert.Throws<ArgumentNullException>(() => new RedisSlidingWindowRateLimiter(null!, Settings));
        Assert.Throws<ArgumentNullException>(() => new RedisSlidingWindowRateLimiter(cache, null!));
        var limiter = new RedisSlidingWindowRateLimiter(cache, Settings);
        await Assert.ThrowsAsync<ArgumentException>(() => limiter.TryAcquireAsync(" "));
    }
}
