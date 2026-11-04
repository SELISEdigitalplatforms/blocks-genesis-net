using Blocks.Genesis;
using Blocks.Genesis.Health;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using StackExchange.Redis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace XUnitTest.Health;

public class GenesisHealthPingBackgroundServiceCoverageTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoadConfigurationFromDatabaseAsync_ShouldApplyConfigAndWriteThroughToCache()
    {
        var databaseName = $"health-ping-tests-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            await client.GetDatabase(databaseName)
                .GetCollection<BlocksServicesHealthConfiguration>("BlocksServicesHealthConfigurations")
                .InsertOneAsync(new BlocksServicesHealthConfiguration
                {
                    ServiceName = "svc-db",
                    Endpoint = "http://127.0.0.1:1/health",
                    HealthCheckEnabled = true,
                    PingIntervalSeconds = 30
                });

            var cache = new Mock<IDatabase>();
            var service = CreateServiceForDatabase(databaseName, cache);

            await InvokePrivateAsync(service, "LoadConfigurationFromDatabaseAsync", CancellationToken.None);

            var current = GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig");
            Assert.NotNull(current);
            Assert.Equal("svc-db", current!.ServiceName);
            Assert.True(current.HealthCheckEnabled);
            Assert.Single(cache.Invocations, i => i.Method.Name == nameof(IDatabase.StringSetAsync));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoadConfigurationFromDatabaseAsync_ShouldKeepConfig_WhenCacheWriteFails()
    {
        var databaseName = $"health-ping-tests-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            await client.GetDatabase(databaseName)
                .GetCollection<BlocksServicesHealthConfiguration>("BlocksServicesHealthConfigurations")
                .InsertOneAsync(new BlocksServicesHealthConfiguration
                {
                    ServiceName = "svc-db",
                    Endpoint = "http://127.0.0.1:1/health",
                    HealthCheckEnabled = true,
                    PingIntervalSeconds = 30
                });

            var cache = new Mock<IDatabase>();
            cache.Setup(c => c.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<Expiration>(),
                    It.IsAny<ValueCondition>(),
                    It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
            var service = CreateServiceForDatabase(databaseName, cache);

            await InvokePrivateAsync(service, "LoadConfigurationFromDatabaseAsync", CancellationToken.None);

            var current = GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig");
            Assert.NotNull(current);
            Assert.Equal("svc-db", current!.ServiceName);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoadConfigurationFromDatabaseAsync_ShouldLeaveConfigNull_WhenDocumentMissing()
    {
        var databaseName = $"health-ping-tests-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            var cache = new Mock<IDatabase>();
            var service = CreateServiceForDatabase(databaseName, cache);

            await InvokePrivateAsync(service, "LoadConfigurationFromDatabaseAsync", CancellationToken.None);

            Assert.Null(GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig"));
            Assert.DoesNotContain(cache.Invocations, i => i.Method.Name == nameof(IDatabase.StringSetAsync));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Theory]
    [InlineData(0, 0, 60)]
    [InlineData(-5, 0, 60)]
    [InlineData(10, 0, 10)]
    [InlineData(10, 1, 3)]
    [InlineData(10, 2, 3)]
    [InlineData(10, 3, 3)]
    [InlineData(10, 4, 10)]
    [InlineData(0, 1, 3)]
    [InlineData(100, 5, 100)]
    public void CalculateDelay_ShouldRetryAfterThreeSecondsThenResumeInterval(int intervalSeconds, int failureCount, int expectedSeconds)
    {
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("CalculateDelay", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var delay = (TimeSpan)method!.Invoke(null, [intervalSeconds, failureCount])!;

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Theory]
    [InlineData(null, "[empty]")]
    [InlineData("", "[empty]")]
    [InlineData("   ", "[empty]")]
    [InlineData("not a url", "[masked]")]
    public void MaskUrl_ShouldHandleEmptyAndInvalidUrls(string? url, string expected)
    {
        Assert.Equal(expected, InvokeMaskUrl(url));
    }

    [Fact]
    public void MaskUrl_ShouldKeepWholePathAsSuffix_WhenPathIsShort()
    {
        Assert.Equal("http://host/***/x", InvokeMaskUrl("http://host/x"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPingAndRetry_ThenStopOnCancellation()
    {
        var config = new BlocksServicesHealthConfiguration
        {
            ServiceName = "svc-loop",
            Endpoint = "http://127.0.0.1:1/health",
            HealthCheckEnabled = true,
            PingIntervalSeconds = 1
        };

        var pinged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateUninitialized();
        SetField(service, "_logger", new Mock<ILogger<GenesisHealthPingBackgroundService>>().Object);
        SetField(service, "_serviceName", "svc-loop");
        SetField(service, "_configKey", "GenesisHealthConfig:svc-loop");
        SetField(service, "_httpClient", new HttpClient(new StubHandler(_ =>
        {
            pinged.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        })));

        var cache = new Mock<IDatabase>();
        cache.Setup(c => c.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
             .ReturnsAsync((RedisValue)JsonSerializer.Serialize(config));
        SetField(service, "_cacheDb", cache.Object);

        using var cts = new CancellationTokenSource();
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var loop = (Task)method!.Invoke(service, [cts.Token])!;

        // Wait until the loop has refreshed config from cache and pinged once, then cancel.
        var completed = await Task.WhenAny(pinged.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(pinged.Task, completed);
        cts.Cancel();
        await loop;

        var current = GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig");
        Assert.NotNull(current);
        Assert.Equal("svc-loop", current!.ServiceName);
    }

    // -------------------------------------------------------------------------
    // Retry with per-attempt timeout: 5s timeout, 3 retries 3s apart, then the
    // normal interval. Real timings; only the startup delay is shortened.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldRetryThreeTimesThreeSecondsApart_ThenResumeInterval_WhenPingKeepsFailing()
    {
        await using var run = PingLoopRun.Start((_, _) => Respond(System.Net.HttpStatusCode.InternalServerError), intervalSeconds: 600);

        Assert.True(await run.WaitForAttemptsAsync(4, TimeSpan.FromSeconds(20)));
        // With a 600s interval, a 5th attempt now could only come from an unbounded retry.
        await Task.Delay(TimeSpan.FromSeconds(5));

        var attempts = run.Attempts;
        Assert.Equal(4, attempts.Count);
        for (var i = 1; i < attempts.Count; i++)
            Assert.InRange((attempts[i].Start - attempts[i - 1].End!.Value).TotalSeconds, 2.5, 4);

        var messages = run.Logger.Messages;
        Assert.Contains(messages, m => m.Level == LogLevel.Warning && m.Text.Contains("retry 1/3 in 3s"));
        Assert.Contains(messages, m => m.Level == LogLevel.Warning && m.Text.Contains("retry 2/3 in 3s"));
        Assert.Contains(messages, m => m.Level == LogLevel.Warning && m.Text.Contains("retry 3/3 in 3s"));
        Assert.Single(messages, m => m.Level == LogLevel.Error && m.Text.Contains("after 3 retries"));
        Assert.DoesNotContain(messages, m => m.Text.Contains(PingLoopRun.SecretPathPart));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResetAndWaitInterval_WhenRetrySucceeds()
    {
        await using var run = PingLoopRun.Start(
            (attempt, _) => Respond(attempt == 0 ? System.Net.HttpStatusCode.InternalServerError : System.Net.HttpStatusCode.OK),
            intervalSeconds: 600);

        Assert.True(await run.WaitForAttemptsAsync(2, TimeSpan.FromSeconds(10)));
        await Task.Delay(TimeSpan.FromSeconds(10));

        var attempts = run.Attempts;
        Assert.Equal(2, attempts.Count);
        Assert.InRange((attempts[1].Start - attempts[0].End!.Value).TotalSeconds, 2.5, 4);
        Assert.DoesNotContain(run.Logger.Messages, m => m.Text.Contains("retry 2/3"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAbandonEachAttemptAfterFiveSeconds_AndRetry_WhenEndpointHangs()
    {
        await using var run = PingLoopRun.Start(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }, intervalSeconds: 600);

        Assert.True(await run.WaitForAttemptsAsync(4, TimeSpan.FromSeconds(35)));
        Assert.True(await run.WaitForCompletedAttemptsAsync(4, TimeSpan.FromSeconds(10)));

        var attempts = run.Attempts;
        foreach (var attempt in attempts)
            Assert.InRange((attempt.End!.Value - attempt.Start).TotalSeconds, 4.5, 6);

        // 4 x 5s timeout + 3 x 3s gap = 29s worst case per cycle.
        Assert.InRange((attempts[3].End!.Value - attempts[0].Start).TotalSeconds, 0, 31);

        var messages = run.Logger.Messages;
        Assert.Equal(4, messages.Count(m => m.Level == LogLevel.Warning && m.Text.Contains("timed out after 5s")));
        Assert.Single(messages, m => m.Level == LogLevel.Error && m.Text.Contains("after 3 retries"));
        Assert.DoesNotContain(messages, m => m.Text.Contains(PingLoopRun.SecretPathPart));
    }

    [Theory]
    [InlineData("client-error")]
    [InlineData("unexpected-exception")]
    [InlineData("network-error")]
    public async Task ExecuteAsync_ShouldRetryAfterThreeSeconds_ForEveryFailureKind(string failure)
    {
        await using var run = PingLoopRun.Start((attempt, _) =>
        {
            if (attempt > 0)
                return Respond(System.Net.HttpStatusCode.OK);

            return failure switch
            {
                "client-error" => Respond(System.Net.HttpStatusCode.BadRequest),
                "network-error" => throw new HttpRequestException("connection refused"),
                _ => throw new InvalidOperationException("boom")
            };
        }, intervalSeconds: 600);

        Assert.True(await run.WaitForAttemptsAsync(2, TimeSpan.FromSeconds(10)));

        var attempts = run.Attempts;
        Assert.InRange((attempts[1].Start - attempts[0].End!.Value).TotalSeconds, 2.5, 4);
        Assert.Contains(run.Logger.Messages, m => m.Level == LogLevel.Warning && m.Text.Contains("retry 1/3 in 3s"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopWithoutFurtherAttempts_WhenCancelledDuringRetryWait()
    {
        var run = PingLoopRun.Start((_, _) => Respond(System.Net.HttpStatusCode.InternalServerError), intervalSeconds: 600);

        Assert.True(await run.WaitForCompletedAttemptsAsync(1, TimeSpan.FromSeconds(10)));
        run.Stop();
        Assert.True(await run.WaitForLoopAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Single(run.Attempts);
        Assert.Contains(run.Logger.Messages, m => m.Text.Contains("Health ping worker stopped"));
        await run.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotLogTimeoutOrRetry_WhenCancelledDuringInFlightRequest()
    {
        var run = PingLoopRun.Start(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }, intervalSeconds: 600);

        Assert.True(await run.WaitForAttemptsAsync(1, TimeSpan.FromSeconds(10)));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        run.Stop();
        Assert.True(await run.WaitForLoopAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Single(run.Attempts);
        var messages = run.Logger.Messages;
        var log = string.Join(Environment.NewLine, messages.Select(m => $"{m.Level}: {m.Text}"));
        Assert.False(messages.Any(m => m.Text.Contains("timed out")), log);
        Assert.False(messages.Any(m => m.Text.Contains("unexpected error")), log);
        // "Ping failed" prefixes both the retry warning and the retries-exhausted error.
        Assert.False(messages.Any(m => m.Text.Contains("Ping failed")), log);
        Assert.Contains(messages, m => m.Text.Contains("Health ping worker stopped"));
        await run.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTakeNoConfigurationBranch_WhenRefreshCannotProduceConfig()
    {
        var service = CreateUninitialized();
        SetField(service, "_logger", new Mock<ILogger<GenesisHealthPingBackgroundService>>().Object);
        SetField(service, "_serviceName", "svc-error");
        SetField(service, "_configKey", "GenesisHealthConfig:svc-error");
        SetField(service, "_httpClient", new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)))));

        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new Mock<IDatabase>();
        cache.Setup(c => c.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
             .Returns(() =>
             {
                 refreshed.TrySetResult();
                 throw new InvalidOperationException("cache exploded");
             });
        SetField(service, "_cacheDb", cache.Object);

        using var cts = new CancellationTokenSource();
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var loop = (Task)method!.Invoke(service, [cts.Token])!;

        // RefreshConfigurationAsync swallows the cache error, leaving config null,
        // so the loop takes the no-configuration branch and waits; cancel there.
        var completed = await Task.WhenAny(refreshed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(refreshed.Task, completed);
        cts.Cancel();
        await loop;

        Assert.Null(GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_ShouldKeepPolling_ThroughNoConfigDisabledAndEmptyEndpointBranches()
    {
        var databaseName = $"health-loop-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            var service = CreateUninitialized();
            SetField(service, "_logger", new Mock<ILogger<GenesisHealthPingBackgroundService>>().Object);
            SetField(service, "_serviceName", "svc-poll");
            SetField(service, "_configKey", "GenesisHealthConfig:svc-poll");
            SetField(service, "_connectionString", MongoConnectionString);
            SetField(service, "_databaseName", databaseName);
            SetField(service, "_startupDelay", TimeSpan.FromMilliseconds(1));
            SetField(service, "_configRefreshInterval", TimeSpan.FromMilliseconds(1));
            SetField(service, "_disabledPollInterval", TimeSpan.FromMilliseconds(10));
            SetField(service, "_httpClient", new HttpClient(new StubHandler(_ =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)))));

            var dbProvider = new Mock<IDbContextProvider>();
            dbProvider.Setup(p => p.GetDatabase(MongoConnectionString, databaseName))
                      .Returns(client.GetDatabase(databaseName));
            SetField(service, "_dbContextProvider", dbProvider.Object);

            var disabledConfig = JsonSerializer.Serialize(new BlocksServicesHealthConfiguration
            {
                ServiceName = "svc-poll", HealthCheckEnabled = false, Endpoint = "http://127.0.0.1:1/h", PingIntervalSeconds = 1
            });
            var emptyEndpointConfig = JsonSerializer.Serialize(new BlocksServicesHealthConfiguration
            {
                ServiceName = "svc-poll", HealthCheckEnabled = true, Endpoint = " ", PingIntervalSeconds = 1
            });

            var reachedLastStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var cache = new Mock<IDatabase>();
            cache.Setup(c => c.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                 .ReturnsAsync(() =>
                 {
                     calls++;
                     if (calls == 1) return RedisValue.Null;          // miss, DB has no doc -> config stays null
                     if (calls == 2) return (RedisValue)"null";       // cached literal null -> falls back to DB
                     if (calls == 3) return (RedisValue)disabledConfig;
                     // A 5th call proves the loop continued past the empty-endpoint wait.
                     if (calls >= 5) reachedLastStage.TrySetResult();
                     return (RedisValue)emptyEndpointConfig;
                 });
            SetField(service, "_cacheDb", cache.Object);

            using var cts = new CancellationTokenSource();
            var method = typeof(GenesisHealthPingBackgroundService).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var loop = (Task)method!.Invoke(service, [cts.Token])!;

            var completed = await Task.WhenAny(reachedLastStage.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(reachedLastStage.Task, completed);
            cts.Cancel();
            await AwaitLoopShutdown(loop);

            Assert.True(calls >= 4);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLogAndContinue_WhenLoopBodyThrowsUnexpectedly()
    {
        var service = CreateUninitialized();
        var caughtError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorCount = 0;

        var logger = new Mock<ILogger<GenesisHealthPingBackgroundService>>();
        // The no-configuration message goes through a LoggerMessage delegate,
        // which checks IsEnabled before logging.
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger.Setup(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("No configuration found")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
              .Throws(new InvalidOperationException("logging pipeline failure"));
        logger.Setup(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Unexpected error in health ping loop")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
              .Callback(() =>
              {
                  // Wait for the second error so the catch block's recovery
                  // delay is proven to complete and the loop to run again.
                  if (Interlocked.Increment(ref errorCount) >= 2)
                  {
                      caughtError.TrySetResult();
                  }
              });

        SetField(service, "_logger", logger.Object);
        SetField(service, "_serviceName", "svc-catch");
        SetField(service, "_configKey", "GenesisHealthConfig:svc-catch");
        SetField(service, "_startupDelay", TimeSpan.FromMilliseconds(1));
        SetField(service, "_configRefreshInterval", TimeSpan.FromMilliseconds(1));
        SetField(service, "_disabledPollInterval", TimeSpan.FromMilliseconds(10));
        SetField(service, "_httpClient", new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)))));

        var cache = new Mock<IDatabase>();
        cache.Setup(c => c.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
             .ThrowsAsync(new InvalidOperationException("cache down"));
        SetField(service, "_cacheDb", cache.Object);

        using var cts = new CancellationTokenSource();
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var loop = (Task)method!.Invoke(service, [cts.Token])!;

        var completed = await Task.WhenAny(caughtError.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(caughtError.Task, completed);
        cts.Cancel();
        await AwaitLoopShutdown(loop);
    }

    // Cancellation can surface either as a clean exit at the while check or as
    // an OperationCanceledException from whichever Task.Delay was in flight.
    private static async Task AwaitLoopShutdown(Task loop)
    {
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static GenesisHealthPingBackgroundService CreateServiceForDatabase(string databaseName, Mock<IDatabase> cache)
    {
        var service = CreateUninitialized();

        var dbProvider = new Mock<IDbContextProvider>();
        dbProvider.Setup(p => p.GetDatabase(MongoConnectionString, databaseName))
                  .Returns(new MongoClient(MongoConnectionString).GetDatabase(databaseName));

        SetField(service, "_logger", new Mock<ILogger<GenesisHealthPingBackgroundService>>().Object);
        SetField(service, "_serviceName", "svc-db");
        SetField(service, "_connectionString", MongoConnectionString);
        SetField(service, "_databaseName", databaseName);
        SetField(service, "_configKey", "GenesisHealthConfig:svc-db");
        SetField(service, "_cacheDb", cache.Object);
        SetField(service, "_dbContextProvider", dbProvider.Object);

        return service;
    }

    private static GenesisHealthPingBackgroundService CreateUninitialized()
    {
        var service = (GenesisHealthPingBackgroundService)RuntimeHelpers.GetUninitializedObject(typeof(GenesisHealthPingBackgroundService));
        // GetUninitializedObject skips field initializers, which would leave the
        // loop timings at TimeSpan.Zero and turn ExecuteAsync into a synchronous
        // infinite loop (Task.Delay(Zero) completes inline and never yields).
        // Restore production-shaped defaults; individual tests shorten them.
        SetField(service, "_startupDelay", TimeSpan.FromSeconds(5));
        SetField(service, "_configRefreshInterval", TimeSpan.FromHours(1));
        SetField(service, "_disabledPollInterval", TimeSpan.FromHours(1));
        return service;
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static T GetField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(instance)!;
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, CancellationToken ct)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method!.Invoke(instance, [ct])!;
    }

    private static string InvokeMaskUrl(string? url)
    {
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("MaskUrl", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [url])!;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }

    private static Task<HttpResponseMessage> Respond(System.Net.HttpStatusCode status)
        => Task.FromResult(new HttpResponseMessage(status));

    /// <summary>
    /// Runs ExecuteAsync against a stub endpoint, recording each attempt's start/end
    /// (UTC) and every log line, so tests can assert retry timing and log content.
    /// </summary>
    private sealed class PingLoopRun : IAsyncDisposable
    {
        public const string SecretPathPart = "SECRET-heartbeat-guid";
        private const string Endpoint = "http://127.0.0.1:1/api/health/ping/" + SecretPathPart + "-tail1234";

        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _responder;
        private readonly List<Attempt> _attempts = [];
        private readonly CancellationTokenSource _cts = new();
        private Task _loop = Task.CompletedTask;

        private PingLoopRun(Func<int, CancellationToken, Task<HttpResponseMessage>> responder) => _responder = responder;

        public CapturingLogger Logger { get; } = new();

        public IReadOnlyList<Attempt> Attempts
        {
            get { lock (_attempts) return _attempts.Select(a => new Attempt(a.Start) { End = a.End }).ToList(); }
        }

        public static PingLoopRun Start(Func<int, CancellationToken, Task<HttpResponseMessage>> responder, int intervalSeconds)
        {
            var run = new PingLoopRun(responder);
            var config = new BlocksServicesHealthConfiguration
            {
                ServiceName = "svc-retry",
                Endpoint = Endpoint,
                HealthCheckEnabled = true,
                PingIntervalSeconds = intervalSeconds
            };

            var service = CreateUninitialized();
            SetField(service, "_logger", run.Logger);
            SetField(service, "_serviceName", "svc-retry");
            SetField(service, "_configKey", "GenesisHealthConfig:svc-retry");
            SetField(service, "_startupDelay", TimeSpan.FromMilliseconds(1));
            // HttpClient keeps its 100s default here: the 5s limit must come from the service itself.
            SetField(service, "_httpClient", new HttpClient(new RecordingHandler(run)));

            var cache = new Mock<IDatabase>();
            cache.Setup(c => c.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                 .ReturnsAsync((RedisValue)JsonSerializer.Serialize(config));
            SetField(service, "_cacheDb", cache.Object);

            var method = typeof(GenesisHealthPingBackgroundService).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            run._loop = (Task)method!.Invoke(service, [run._cts.Token])!;
            return run;
        }

        public void Stop() => _cts.Cancel();

        public Task<bool> WaitForAttemptsAsync(int count, TimeSpan timeout)
            => WaitUntilAsync(() => Attempts.Count >= count, timeout);

        public Task<bool> WaitForCompletedAttemptsAsync(int count, TimeSpan timeout)
            => WaitUntilAsync(() => Attempts.Count(a => a.End.HasValue) >= count, timeout);

        public async Task<bool> WaitForLoopAsync(TimeSpan timeout)
            => await Task.WhenAny(_loop, Task.Delay(timeout)) == _loop;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _loop;
            _cts.Dispose();
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                await Task.Delay(20);
            }
            return condition();
        }

        private sealed class RecordingHandler(PingLoopRun run) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var attempt = new Attempt(DateTime.UtcNow);
                int index;
                lock (run._attempts)
                {
                    index = run._attempts.Count;
                    run._attempts.Add(attempt);
                }

                try
                {
                    return await run._responder(index, cancellationToken);
                }
                finally
                {
                    lock (run._attempts)
                        attempt.End = DateTime.UtcNow;
                }
            }
        }
    }

    private sealed class Attempt(DateTime start)
    {
        public DateTime Start { get; } = start;
        public DateTime? End { get; set; }
    }

    private sealed class CapturingLogger : ILogger<GenesisHealthPingBackgroundService>
    {
        private readonly List<(LogLevel Level, string Text)> _messages = [];

        public IReadOnlyList<(LogLevel Level, string Text)> Messages
        {
            get { lock (_messages) return _messages.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
                _messages.Add((logLevel, formatter(state, exception)));
        }
    }
}
