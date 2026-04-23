using Blocks.Genesis;
using Blocks.Genesis.Health;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using StackExchange.Redis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Health;

public class GenesisHealthPingBackgroundServiceAdditionalTests
{
    [Fact]
    public async Task LoadConfigurationFromDatabaseAsync_ShouldLogWarning_WhenNoConfigFound()
    {
        var service = CreateUninitializedService(out var logger, out var cache, out var dbProvider);

        var cursor = CreateCursor(Array.Empty<BlocksServicesHealthConfiguration>());
        var collection = new Mock<IMongoCollection<BlocksServicesHealthConfiguration>>();
        collection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BlocksServicesHealthConfiguration>>(),
                It.IsAny<FindOptions<BlocksServicesHealthConfiguration, BlocksServicesHealthConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var database = new Mock<IMongoDatabase>();
        database
            .Setup(d => d.GetCollection<BlocksServicesHealthConfiguration>(
                "BlocksServicesHealthConfigurations",
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        dbProvider
            .Setup(p => p.GetDatabase(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(database.Object);

        await InvokePrivateAsync(service, "LoadConfigurationFromDatabaseAsync", CancellationToken.None);

        // No config → _currentConfig stays null, no cache write.
        Assert.Null(GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig"));
        cache.Verify(
            c => c.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadConfigurationFromDatabaseAsync_ShouldApplyConfigAndWriteThroughCache_WhenFound()
    {
        var service = CreateUninitializedService(out _, out var cache, out var dbProvider);

        var cfg = new BlocksServicesHealthConfiguration
        {
            ServiceName = "svc-loaded",
            Endpoint = "https://api.example.com/health",
            HealthCheckEnabled = true,
            PingIntervalSeconds = 30
        };
        var cursor = CreateCursor(new[] { cfg });

        var collection = new Mock<IMongoCollection<BlocksServicesHealthConfiguration>>();
        collection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BlocksServicesHealthConfiguration>>(),
                It.IsAny<FindOptions<BlocksServicesHealthConfiguration, BlocksServicesHealthConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var database = new Mock<IMongoDatabase>();
        database
            .Setup(d => d.GetCollection<BlocksServicesHealthConfiguration>(
                "BlocksServicesHealthConfigurations",
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        dbProvider
            .Setup(p => p.GetDatabase(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(database.Object);

        cache
            .Setup(c => c.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        await InvokePrivateAsync(service, "LoadConfigurationFromDatabaseAsync", CancellationToken.None);

        var applied = GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig");
        Assert.NotNull(applied);
        Assert.Equal("svc-loaded", applied!.ServiceName);

        cache.Verify(
            c => c.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.Is<TimeSpan?>(t => t.HasValue && t.Value == TimeSpan.FromMinutes(15)),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task LoadConfigurationFromDatabaseAsync_ShouldSwallowCacheError_WhenWriteThroughFails()
    {
        var service = CreateUninitializedService(out _, out var cache, out var dbProvider);

        var cfg = new BlocksServicesHealthConfiguration
        {
            ServiceName = "svc-cache-fail",
            Endpoint = "https://api.example.com/health",
            HealthCheckEnabled = true,
            PingIntervalSeconds = 30
        };
        var cursor = CreateCursor(new[] { cfg });

        var collection = new Mock<IMongoCollection<BlocksServicesHealthConfiguration>>();
        collection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BlocksServicesHealthConfiguration>>(),
                It.IsAny<FindOptions<BlocksServicesHealthConfiguration, BlocksServicesHealthConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var database = new Mock<IMongoDatabase>();
        database
            .Setup(d => d.GetCollection<BlocksServicesHealthConfiguration>(
                "BlocksServicesHealthConfigurations",
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        dbProvider
            .Setup(p => p.GetDatabase(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(database.Object);

        cache
            .Setup(c => c.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisException("cache down"));

        var exception = await Record.ExceptionAsync(() =>
            InvokePrivateAsync(service, "LoadConfigurationFromDatabaseAsync", CancellationToken.None));

        Assert.Null(exception);
        // Config is still applied in-memory even though cache write failed.
        var applied = GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig");
        Assert.NotNull(applied);
        Assert.Equal("svc-cache-fail", applied!.ServiceName);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldEnterNullConfigPollBranch_WhenNoConfigAvailable()
    {
        // Cache miss + DB returns nothing → _currentConfig stays null.
        // ExecuteAsync should hit the "No configuration found" branch, await the
        // DisabledPollInterval (1h), and exit cleanly when the stoppingToken cancels.
        var cacheDb = new Mock<IDatabase>();
        cacheDb
            .Setup(c => c.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.CacheDatabase()).Returns(cacheDb.Object);

        var cursor = CreateCursor(Array.Empty<BlocksServicesHealthConfiguration>());
        var collection = new Mock<IMongoCollection<BlocksServicesHealthConfiguration>>();
        collection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BlocksServicesHealthConfiguration>>(),
                It.IsAny<FindOptions<BlocksServicesHealthConfiguration, BlocksServicesHealthConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var database = new Mock<IMongoDatabase>();
        database
            .Setup(d => d.GetCollection<BlocksServicesHealthConfiguration>(
                "BlocksServicesHealthConfigurations",
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        var dbProvider = new Mock<IDbContextProvider>();
        dbProvider.Setup(p => p.GetDatabase(It.IsAny<string>(), It.IsAny<string>())).Returns(database.Object);

        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(s => s.ServiceName).Returns("svc-null-cfg");
        blocksSecret.SetupGet(s => s.DatabaseConnectionString).Returns("mongodb://localhost:27017");
        blocksSecret.SetupGet(s => s.RootDatabaseName).Returns("root-db");

        var service = new GenesisHealthPingBackgroundService(
            Mock.Of<ILogger<GenesisHealthPingBackgroundService>>(),
            dbProvider.Object,
            cacheClient.Object,
            blocksSecret.Object);

        var execute = typeof(GenesisHealthPingBackgroundService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var task = (Task)execute.Invoke(service, [cts.Token])!;
        await task;

        Assert.Null(GetField<BlocksServicesHealthConfiguration?>(service, "_currentConfig"));
    }

    private static GenesisHealthPingBackgroundService CreateUninitializedService(
        out Mock<ILogger<GenesisHealthPingBackgroundService>> logger,
        out Mock<IDatabase> cache,
        out Mock<IDbContextProvider> dbProvider)
    {
        var service = (GenesisHealthPingBackgroundService)RuntimeHelpers.GetUninitializedObject(typeof(GenesisHealthPingBackgroundService));

        logger = new Mock<ILogger<GenesisHealthPingBackgroundService>>();
        cache = new Mock<IDatabase>();
        dbProvider = new Mock<IDbContextProvider>();

        SetField(service, "_logger", logger.Object);
        SetField(service, "_cacheDb", cache.Object);
        SetField(service, "_dbContextProvider", dbProvider.Object);
        SetField(service, "_serviceName", "svc-additional");
        SetField(service, "_connectionString", "mongodb://localhost:27017");
        SetField(service, "_databaseName", "root-db");
        SetField(service, "_configKey", "GenesisHealthConfig:svc-additional");

        return service;
    }

    private static Mock<IAsyncCursor<BlocksServicesHealthConfiguration>> CreateCursor(
        IEnumerable<BlocksServicesHealthConfiguration> docs)
    {
        var cursor = new Mock<IAsyncCursor<BlocksServicesHealthConfiguration>>();
        var list = docs.ToArray();
        var moved = false;

        cursor
            .Setup(c => c.MoveNext(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (moved) return false;
                moved = true;
                return list.Length > 0;
            });
        cursor
            .Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (moved) return false;
                moved = true;
                return list.Length > 0;
            });
        cursor.SetupGet(c => c.Current).Returns(list);
        return cursor;
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(instance, args)!;
        await task;
    }

    private static T GetField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(instance)!;
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }
}
