using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Blocks.Genesis;

/// <summary>
/// Waits for a dependency (RabbitMQ, Azure Service Bus) while a service starts. The connect-and-declare work is
/// retried with exponential backoff, 1 s growing to 30 s, until it works or the total wait is used up.
/// The total wait is read from the environment variable <c>Startup__DependencyWaitSeconds</c> (default 120;
/// 0 means one attempt and no waiting). Every failed attempt writes one log line. When the wait is used up, or the
/// failure is not worth retrying, the original exception is rethrown.
/// </summary>
internal static class StartupRetry
{
    internal const string WaitSecondsVariable = "Startup__DependencyWaitSeconds";
    internal const int DefaultWaitSeconds = 120;
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);

    private static readonly Lazy<ILogger> FallbackLogger = new(() =>
        LoggerFactory.Create(builder => builder.AddSerilog(
            new LoggerConfiguration().WriteTo.Console().CreateLogger(), dispose: true))
            .CreateLogger("Blocks.Genesis.StartupRetry"));

    /// <summary>The caller's logger, or a console logger when the caller (static start-up code) has none.</summary>
    internal static ILogger LoggerOrDefault(ILogger? logger) => logger ?? FallbackLogger.Value;

    /// <summary>Total wait from <c>Startup__DependencyWaitSeconds</c>; a missing, non-numeric or negative value gives the default.</summary>
    internal static TimeSpan ReadTotalWait()
    {
        var raw = Environment.GetEnvironmentVariable(WaitSecondsVariable);
        return int.TryParse(raw, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(DefaultWaitSeconds);
    }

    /// <summary>The failure and its innermost cause, for one log line (a broker error often hides the real cause one level down).</summary>
    internal static string Describe(Exception? exception)
    {
        if (exception is null) return "unknown";
        var inner = exception;
        while (inner.InnerException is { } next) inner = next;
        if (inner is AggregateException { InnerExceptions.Count: > 0 } aggregate) inner = aggregate.InnerExceptions[0];
        return ReferenceEquals(inner, exception)
            ? exception.Message
            : $"{exception.Message} ({inner.GetType().Name}: {inner.Message})";
    }

    internal static async Task ExecuteAsync(
        string dependency,
        Func<CancellationToken, Task> attempt,
        ILogger logger,
        Func<Exception, bool> isTransient,
        TimeSpan? totalWait = null,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        CancellationToken cancellationToken = default)
    {
        var budget = totalWait ?? ReadTotalWait();
        var first = initialDelay ?? DefaultInitialDelay;
        var cap = maxDelay ?? DefaultMaxDelay;
        var clock = Stopwatch.StartNew();

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Exponential,
                Delay = first,
                MaxDelay = cap,
                UseJitter = false,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is { } ex && isTransient(ex) && clock.Elapsed < budget),
                // Same curve as the options above (1, 2, 4 ... capped), shortened so the last wait never passes the total.
                DelayGenerator = args =>
                {
                    var step = TimeSpan.FromTicks((long)Math.Min(cap.Ticks, first.Ticks * Math.Pow(2, Math.Min(args.AttemptNumber, 30))));
                    var remaining = budget - clock.Elapsed;
                    return new ValueTask<TimeSpan?>(step < remaining ? step : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                },
                OnRetry = args =>
                {
                    var remaining = budget - clock.Elapsed - args.RetryDelay;
                    logger.LogWarning(
                        "{Dependency} is not ready (attempt {Attempt} failed: {Reason}). Retrying in {DelaySeconds:0.#} s; about {RemainingSeconds:0} s of the {TotalSeconds:0} s start-up wait are left ({Variable}).",
                        dependency, args.AttemptNumber + 1, Describe(args.Outcome.Exception), args.RetryDelay.TotalSeconds,
                        remaining > TimeSpan.Zero ? remaining.TotalSeconds : 0, budget.TotalSeconds, WaitSecondsVariable);
                    return default;
                }
            })
            .Build();

        try
        {
            await pipeline.ExecuteAsync(async token => await attempt(token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("{Dependency} is still not usable after {ElapsedSeconds:0} s (start-up wait {TotalSeconds:0} s). Giving up: {Reason}",
                dependency, clock.Elapsed.TotalSeconds, budget.TotalSeconds, Describe(ex));
            throw;
        }
    }
}
