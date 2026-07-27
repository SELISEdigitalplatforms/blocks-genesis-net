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
        return (GenesisHealthPingBackgroundService)RuntimeHelpers.GetUninitializedObject(typeof(GenesisHealthPingBackgroundService));
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
