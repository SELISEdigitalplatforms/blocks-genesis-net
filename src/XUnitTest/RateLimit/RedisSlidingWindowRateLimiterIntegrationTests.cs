using Blocks.Genesis;
using StackExchange.Redis;
using System.Diagnostics;

namespace XUnitTest.RateLimit;

/// <summary>The Lua sliding window against a real Redis.</summary>
public class RedisSlidingWindowRateLimiterIntegrationTests
{
    private static string NewService() => $"svc-it-{Guid.NewGuid():N}";

    private static RedisSlidingWindowRateLimiter Limiter(string service, int limit = 20, ConnectionMultiplexer? multiplexer = null)
    {
        var (client, _) = RedisTestServer.CountingClient(multiplexer);
        return new RedisSlidingWindowRateLimiter(
            client,
            new UserRateLimitSettings(limit, UserRateLimitSettings.EnvironmentSource, service),
            null,
            RedisTestServer.Relaxed);
    }

    private static async Task<List<RateLimitDecision>> Burst(IUserRateLimiter limiter, string key, int count)
    {
        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => limiter.TryAcquireAsync(key)));
        return results.Select(r => r!.Value).ToList();
    }

    [Fact]
    public async Task LimitPlusOne_AllowsLimit_ThenStartsCooldown() // H5, E3
    {
        var service = NewService();
        var key = $"ratelimit:{service}:t1:user:u1";
        var limiter = Limiter(service);
        await RedisTestServer.WaitForWindowOffsetAsync(0, 400);

        var decisions = new List<RateLimitDecision>();
        for (var i = 0; i < 21; i++)
        {
            decisions.Add((await limiter.TryAcquireAsync(key))!.Value);
        }

        Assert.All(decisions.Take(20), d => Assert.True(d.Allowed));
        Assert.Equal(Enumerable.Range(0, 20).Reverse(), decisions.Take(20).Select(d => d.Remaining));
        Assert.All(decisions.Take(20), d => Assert.Equal(1, d.ResetSeconds));
        Assert.Equal(new RateLimitDecision(false, 0, 30), decisions[20]);

        var cooldownEnd = (long)await RedisTestServer.Database.HashGetAsync(key, "cu");
        var time = (RedisResult[])(await RedisTestServer.Database.ExecuteAsync("TIME"))!;
        var nowMs = (long)time[0] * 1000 + (long)time[1] / 1000;
        Assert.InRange(cooldownEnd - nowMs, 29_000, 30_000);
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task Cooldown_RejectsWithRemainingTime_IsNotExtended_ThenAllows() // C5, H11, E4
    {
        var service = NewService();
        var key = $"ratelimit:{service}:t1:user:u1";
        var limiter = Limiter(service);
        await Burst(limiter, key, 21);
        var cooldownEnd = (long)await RedisTestServer.Database.HashGetAsync(key, "cu");

        await Task.Delay(TimeSpan.FromSeconds(10));
        var during = (await limiter.TryAcquireAsync(key))!.Value;
        Assert.False(during.Allowed);
        Assert.InRange(during.ResetSeconds, 19, 21);
        Assert.Equal(cooldownEnd, (long)await RedisTestServer.Database.HashGetAsync(key, "cu"));

        await Task.Delay(TimeSpan.FromSeconds(21));
        var after = (await limiter.TryAcquireAsync(key))!.Value;
        Assert.True(after.Allowed);
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task WindowBoundary_AllowsAtMostTenPercentOfSecondBatch() // C6, E7
    {
        var service = NewService();
        var key = $"ratelimit:{service}:t1:user:u1";
        var limiter = Limiter(service);
        await limiter.TryAcquireAsync($"ratelimit:{service}:warmup:user:w");

        await RedisTestServer.WaitForWindowOffsetAsync(850, 900);
        var first = await Burst(limiter, key, 20);
        await RedisTestServer.WaitForWindowOffsetAsync(100, 115);
        var second = await Burst(limiter, key, 20);
        var burstEnd = await RedisTestServer.MillisecondsIntoWindowAsync();

        Assert.All(first, d => Assert.True(d.Allowed));
        var allowed = second.Count(d => d.Allowed);
        // Within the first 10% (+ the burst's own duration) at most ceil(limit x 0.1) pass. If a slow
        // runner stretched the burst, the bound follows the measured offset instead.
        var bound = burstEnd <= 150 ? (int)Math.Ceiling(20 * 0.1) : (int)Math.Ceiling(20 * burstEnd / 1000.0);
        Assert.InRange(allowed, 1, bound);
        Assert.Equal(20 - allowed, second.Count(d => !d.Allowed));
        Assert.True(await RedisTestServer.Database.HashExistsAsync(key, "cu"));
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task ThreeInstancesSharingRedis_AllowExactlyTheLimit() // C7
    {
        var service = NewService();
        var key = $"ratelimit:{service}:t1:user:u1";
        using var m1 = RedisTestServer.Connect();
        using var m2 = RedisTestServer.Connect();
        using var m3 = RedisTestServer.Connect();
        var limiters = new[] { Limiter(service, multiplexer: m1), Limiter(service, multiplexer: m2), Limiter(service, multiplexer: m3) };
        foreach (var l in limiters)
        {
            await l.TryAcquireAsync($"ratelimit:{service}:warmup:user:w");
        }

        await RedisTestServer.WaitForWindowOffsetAsync(0, 300);
        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => limiters[i % 3].TryAcquireAsync(key)));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(20, results.Count(r => r!.Value.Allowed));
        Assert.Equal(40, results.Count(r => !r!.Value.Allowed));
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task SteadyNinetyPercent_ForSixtySeconds_IsNeverRejected() // H10
    {
        var service = NewService();
        var key = $"ratelimit:{service}:t1:user:u1";
        var limiter = Limiter(service);
        var interval = TimeSpan.FromMilliseconds(1000.0 / 18);
        var watch = Stopwatch.StartNew();
        var rejected = 0;
        var sent = 0;

        var next = TimeSpan.Zero;
        for (var i = 0; i < 18 * 60; i++)
        {
            // Evenly spaced and never bunched: a late send pushes the schedule back instead of
            // catching up, so the rate never exceeds 18/s even on a slow runner.
            var wait = next - watch.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }

            next = watch.Elapsed + interval;

            var decision = await limiter.TryAcquireAsync(key);
            sent++;
            if (decision is { Allowed: false })
            {
                rejected++;
            }
        }

        Assert.Equal(1080, sent);
        Assert.Equal(0, rejected);
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task IdleKey_Expires_AndOldTrafficIsForgotten() // C16, C18
    {
        var service = NewService();
        var key = $"ratelimit:{service}:t1:user:u1";
        var limiter = Limiter(service);

        for (var i = 0; i < 5; i++)
        {
            await limiter.TryAcquireAsync(key);
        }

        var ttl = await RedisTestServer.Database.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalMilliseconds, 1, 3000);

        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        Assert.True(await RedisTestServer.Database.KeyExistsAsync(key));
        await limiter.TryAcquireAsync(key);
        Assert.Equal(0, (long)await RedisTestServer.Database.HashGetAsync(key, "pc"));
        Assert.Equal(1, (long)await RedisTestServer.Database.HashGetAsync(key, "cc"));

        await Task.Delay(TimeSpan.FromMilliseconds(3200));
        Assert.False(await RedisTestServer.Database.KeyExistsAsync(key));
    }

    [Fact]
    public async Task CooldownKey_TtlCoversTheCooldown() // C16
    {
        var service = NewService();
        var key = $"ratelimit:{service}:t1:user:u1";
        var limiter = Limiter(service, limit: 2);

        await Burst(limiter, key, 3);

        var ttl = await RedisTestServer.Database.KeyTimeToLiveAsync(key);
        Assert.InRange(ttl!.Value.TotalMilliseconds, 29_000, 31_000);
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task SeparateSubjectsAndTenants_KeepSeparateCounts() // H9
    {
        var service = NewService();
        var limiter = Limiter(service, limit: 2);

        await Burst(limiter, $"ratelimit:{service}:t1:user:u1", 3);

        Assert.True((await limiter.TryAcquireAsync($"ratelimit:{service}:t1:user:u2"))!.Value.Allowed);
        Assert.True((await limiter.TryAcquireAsync($"ratelimit:{service}:t2:user:u1"))!.Value.Allowed);
        Assert.False((await limiter.TryAcquireAsync($"ratelimit:{service}:t1:user:u1"))!.Value.Allowed);
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }
}
