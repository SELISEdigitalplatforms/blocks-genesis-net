using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using StackExchange.Redis;

namespace Blocks.Genesis;

/// <summary>
/// Sliding window counter with a cooldown, stored as one Redis hash per subject and evaluated
/// atomically by a Lua script. Fails open: any Redis problem allows the request.
/// </summary>
public sealed class RedisSlidingWindowRateLimiter : IUserRateLimiter
{
    /// <summary>
    /// KEYS[1] = key. ARGV = limit, windowMs, cost, cooldownMs. Time comes from Redis TIME so pod
    /// clocks never matter. Returns {allowed, remaining, resetSeconds}.
    /// </summary>
    internal const string Script = """
        if redis.replicate_commands then redis.replicate_commands() end
        local key = KEYS[1]
        local limit = tonumber(ARGV[1])
        local windowMs = tonumber(ARGV[2])
        local cost = tonumber(ARGV[3])
        local cooldownMs = tonumber(ARGV[4])
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local data = redis.call('HMGET', key, 'ws', 'cc', 'pc', 'cu')
        local cu = tonumber(data[4])
        if cu and now < cu then
          return {0, 0, math.ceil((cu - now) / 1000)}
        end
        local cur = math.floor(now / windowMs) * windowMs
        local ws = tonumber(data[1])
        local cc = tonumber(data[2]) or 0
        local pc = tonumber(data[3]) or 0
        if not ws then
          ws = cur
          cc = 0
          pc = 0
        elseif cur - ws == windowMs then
          pc = cc
          cc = 0
          ws = cur
        elseif cur - ws > windowMs then
          pc = 0
          cc = 0
          ws = cur
        end
        local elapsed = (now - ws) / windowMs
        local estimated = pc * (1 - elapsed) + cc
        if estimated + cost > limit then
          cu = now + cooldownMs
          redis.call('HSET', key, 'ws', ws, 'cc', cc, 'pc', pc, 'cu', cu)
          redis.call('PEXPIRE', key, math.max(2 * windowMs, cooldownMs) + 1000)
          return {0, 0, math.floor(cooldownMs / 1000)}
        end
        cc = cc + cost
        redis.call('HSET', key, 'ws', ws, 'cc', cc, 'pc', pc)
        redis.call('HDEL', key, 'cu')
        redis.call('PEXPIRE', key, 2 * windowMs + 1000)
        return {1, math.max(0, math.floor(limit - estimated - cost)), math.ceil((ws + windowMs - now) / 1000)}
        """;

    internal const int Cost = 1;

    private readonly ICacheClient _cacheClient;
    private readonly UserRateLimitSettings _settings;
    private readonly ResiliencePipeline<RedisResult> _pipeline;

    public RedisSlidingWindowRateLimiter(
        ICacheClient cacheClient,
        UserRateLimitSettings settings,
        ILogger<RedisSlidingWindowRateLimiter>? logger = null)
        : this(cacheClient, settings, logger, RedisRateLimitResilienceOptions.Default)
    {
    }

    internal RedisSlidingWindowRateLimiter(
        ICacheClient cacheClient,
        UserRateLimitSettings settings,
        ILogger? logger,
        RedisRateLimitResilienceOptions resilience)
    {
        _cacheClient = cacheClient ?? throw new ArgumentNullException(nameof(cacheClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _pipeline = BuildPipeline(resilience ?? RedisRateLimitResilienceOptions.Default, logger ?? NullLogger.Instance);
    }

    /// <inheritdoc />
    public async Task<RateLimitDecision?> TryAcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var result = await _pipeline.ExecuteAsync(
                static async (state, token) =>
                    await state.Database
                        .ScriptEvaluateAsync(Script, state.Keys, state.Values)
                        .WaitAsync(token)
                        .ConfigureAwait(false),
                new ScriptCall(_cacheClient.CacheDatabase(), [new RedisKey(key)], BuildArguments(_settings.PermitLimit)),
                cancellationToken).ConfigureAwait(false);

            return ParseResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            // Fail open: a limiter problem must never fail the request.
            return null;
        }
    }

    /// <summary>
    /// The ARGV the script receives. There is deliberately no time argument (C12).
    /// </summary>
    internal static RedisValue[] BuildArguments(int permitLimit) =>
    [
        permitLimit,
        BlocksConstants.RateLimitWindowSeconds * 1000,
        Cost,
        BlocksConstants.RateLimitRetryAfterSeconds * 1000
    ];

    internal static RateLimitDecision? ParseResult(RedisResult? result)
    {
        if (result is null || result.IsNull || result.Resp2Type != ResultType.Array)
        {
            return null;
        }

        var values = (RedisResult[]?)result;
        if (values is null || values.Length < 3)
        {
            return null;
        }

        return new RateLimitDecision(
            Allowed: (long)values[0] == 1,
            Remaining: (int)Math.Max(0, (long)values[1]),
            ResetSeconds: (int)Math.Max(0, (long)values[2]));
    }

    private static bool IsRedisFailure(Exception exception) =>
        exception is RedisException
            or TimeoutException
            or TimeoutRejectedException
            or BrokenCircuitException
            or OperationCanceledException
            or ObjectDisposedException
            or InvalidCastException;

    private static ResiliencePipeline<RedisResult> BuildPipeline(RedisRateLimitResilienceOptions options, ILogger logger)
    {
        var handled = new PredicateBuilder<RedisResult>()
            .Handle<RedisException>()
            .Handle<TimeoutException>()
            .Handle<TimeoutRejectedException>()
            .Handle<ObjectDisposedException>();

        return new ResiliencePipelineBuilder<RedisResult>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RedisResult>
            {
                FailureRatio = options.FailureRatio,
                SamplingDuration = options.SamplingDuration,
                MinimumThroughput = options.MinimumThroughput,
                BreakDuration = options.BreakDuration,
                ShouldHandle = handled,
                OnOpened = _ =>
                {
                    UserRateLimitLog.CircuitOpened(logger);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    UserRateLimitLog.CircuitClosed(logger);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(options.Timeout)
            .Build();
    }

    private sealed record ScriptCall(IDatabase Database, RedisKey[] Keys, RedisValue[] Values);
}

/// <summary>
/// Internal resilience constants for the Redis check (spec A5). Tests use shorter values.
/// </summary>
internal sealed record RedisRateLimitResilienceOptions(
    TimeSpan Timeout,
    double FailureRatio,
    TimeSpan SamplingDuration,
    int MinimumThroughput,
    TimeSpan BreakDuration)
{
    public static RedisRateLimitResilienceOptions Default { get; } = new(
        Timeout: TimeSpan.FromMilliseconds(BlocksConstants.RateLimitRedisTimeoutMilliseconds),
        FailureRatio: 0.5,
        SamplingDuration: TimeSpan.FromSeconds(10),
        MinimumThroughput: 10,
        BreakDuration: TimeSpan.FromSeconds(15));
}
