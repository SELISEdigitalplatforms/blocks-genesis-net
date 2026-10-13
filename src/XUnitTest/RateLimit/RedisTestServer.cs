using Blocks.Genesis;
using Moq;
using StackExchange.Redis;

namespace XUnitTest.RateLimit;

/// <summary>
/// Real Redis on localhost:6379 (docker-compose locally, the redis:7.4 service in CI).
/// </summary>
internal static class RedisTestServer
{
    private static readonly Lazy<ConnectionMultiplexer> Shared = new(() => Connect());

    public static ConnectionMultiplexer Connect() =>
        ConnectionMultiplexer.Connect("localhost:6379,abortConnect=false,connectTimeout=5000");

    public static IDatabase Database => Shared.Value.GetDatabase();

    /// <summary>An ICacheClient over a real Redis that counts how many times the limiter used it.</summary>
    public static (ICacheClient Client, Func<int> Calls) CountingClient(ConnectionMultiplexer? multiplexer = null)
    {
        var calls = 0;
        var database = (multiplexer ?? Shared.Value).GetDatabase();
        var client = new Mock<ICacheClient>();
        client.Setup(c => c.CacheDatabase()).Returns(() =>
        {
            Interlocked.Increment(ref calls);
            return database;
        });
        return (client.Object, () => Volatile.Read(ref calls));
    }

    public static async Task FlushAsync(string pattern)
    {
        var server = Shared.Value.GetServer(Shared.Value.GetEndPoints()[0]);
        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            await Database.KeyDeleteAsync(key);
        }
    }

    /// <summary>Milliseconds into the current 1 s window, by Redis TIME.</summary>
    public static async Task<long> MillisecondsIntoWindowAsync()
    {
        var time = (RedisResult[])(await Database.ExecuteAsync("TIME"))!;
        var nowMs = (long)time[0] * 1000 + (long)time[1] / 1000;
        return nowMs % 1000;
    }

    /// <summary>Waits until Redis TIME is within [from, to) ms of a window.</summary>
    public static async Task WaitForWindowOffsetAsync(long from, long to)
    {
        while (true)
        {
            var offset = await MillisecondsIntoWindowAsync();
            if (offset >= from && offset < to)
            {
                return;
            }

            await Task.Delay(1);
        }
    }

    /// <summary>Generous timeout for burst tests so slow CI runners measure the algorithm, not latency.</summary>
    public static RedisRateLimitResilienceOptions Relaxed { get; } =
        RedisRateLimitResilienceOptions.Default with { Timeout = TimeSpan.FromSeconds(2) };
}
