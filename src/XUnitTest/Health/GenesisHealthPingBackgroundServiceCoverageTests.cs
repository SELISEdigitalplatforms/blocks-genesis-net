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
    [InlineData(10, 1, 20)]
    [InlineData(10, 2, 40)]
    [InlineData(100, 5, 300)]
    public void CalculateDelay_ShouldApplyDefaultsAndExponentialBackoffWithCap(int intervalSeconds, int failureCount, int expectedSeconds)
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
    public async Task ExecuteAsync_ShouldPingAndBackoff_ThenStopOnCancellation()
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
}
