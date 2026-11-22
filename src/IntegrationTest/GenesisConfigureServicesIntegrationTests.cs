using System.Collections.Concurrent;
using System.Reflection;
using Blocks.Genesis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson.Serialization;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace IntegrationTest;

/// <summary>
/// Genesis <c>ApplicationConfigurations.ConfigureServices</c> against real MongoDB, Redis and RabbitMQ.
/// Images are the ones the local stack already uses (mongo:8, redis:8-alpine, rabbitmq:3-management).
/// ConfigureServices keeps its settings in static fields, so the tests share one collection and run one at a time.
/// Where Docker is reached through a mounted socket (the T35 test runner), set TESTCONTAINERS_HOST_OVERRIDE
/// to the host's name for containers (host.docker.internal) and TESTCONTAINERS_RYUK_DISABLED=true when the
/// Ryuk image is not on the machine; the tests dispose their containers themselves.
/// </summary>
[Collection("Genesis statics")]
[Trait("Category", "Integration")]
public sealed class GenesisConfigureServicesIntegrationTests : IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();

    public async Task InitializeAsync() => await Task.WhenAll(_mongo.StartAsync(), _redis.StartAsync());

    public async Task DisposeAsync()
    {
        await _mongo.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task ConfigureServices_Succeeds_WhenMongoRedisAndRabbitMqAreUp()
    {
        await using var rabbit = new RabbitMqBuilder("rabbitmq:3-management").Build();
        await rabbit.StartAsync();

        var services = ConfigureGenesis(rabbit.GetConnectionString());

        Assert.Contains(services, d => d.ServiceType == typeof(IRabbitMqService));

        // The real MongoDB and Redis answer the health checks that ConfigureServices registered.
        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(HealthStatus.Healthy, report.Entries["mongodb"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["redis"].Status);
    }

    /// <summary>
    /// Acceptance test for I01-WP1 (startup should wait for RabbitMQ instead of failing once). RabbitMQ comes up
    /// 10 seconds after the host starts configuring. Today ConfigureRabbitMq connects once and rethrows
    /// (ConfigureRabbitMq.cs, the CreateConnectionAsync call and the catch), so this FAILS with a connect
    /// failure. Not skipped: it is excluded from the L1 gate with Category=KnownRed until I01-WP1 lands.
    /// </summary>
    [Fact]
    [Trait("Category", "KnownRed")]
    public async Task ConfigureServices_Succeeds_WhenRabbitMqStarts10SecondsAfterTheHost()
    {
        var hostName = Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE") is { Length: > 0 } h ? h : "localhost";
        var hostPort = Random.Shared.Next(30000, 40000);
        await using var rabbit = new RabbitMqBuilder("rabbitmq:3-management")
            .WithPortBinding(hostPort, 5672)
            .Build();
        var connection = $"amqp://guest:guest@{hostName}:{hostPort}";

        // The host starts configuring now, with RabbitMQ not there yet.
        var host = Task.Run(() => ConfigureGenesis(connection));

        await Task.Delay(TimeSpan.FromSeconds(10));
        await rabbit.StartAsync();

        // A host that waits for its dependencies finishes once RabbitMQ is up. Awaiting rethrows today's failure.
        var services = await host.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Contains(services, d => d.ServiceType == typeof(IRabbitMqService));
    }

    private ServiceCollection ConfigureGenesis(string rabbitConnection)
    {
        SetPrivateStaticField("_serviceName", "svc-integration-test");
        SetPrivateStaticField("_blocksSwaggerOptions", null);
        SetPrivateStaticField("_blocksSecret", new BlocksSecret
        {
            DatabaseConnectionString = $"mongodb://{_mongo.Hostname}:{_mongo.GetMappedPublicPort(27017)}/genesis-integration-tests",
            CacheConnectionString = $"{_redis.GetConnectionString()},abortConnect=false",
            MessageConnectionString = string.Empty,
            TraceConnectionString = string.Empty
        });
        RemoveRegisteredObjectBsonSerializer();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var rabbitConfiguration = new Blocks.Genesis.RabbitMqConfiguration();
        rabbitConfiguration.ConsumerSubscriptions.Add(new ConsumerSubscription("it-queue", "it-exchange", 1));
        ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration
        {
            Connection = rabbitConnection,
            ServiceName = "svc-integration-test",
            RabbitMqConfiguration = rabbitConfiguration
        });
        return services;
    }

    private static void SetPrivateStaticField(string fieldName, object? value)
    {
        var field = typeof(ApplicationConfigurations).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, value);
    }

    // ConfigureServices registers a global object serializer; a second registration in one process throws.
    private static void RemoveRegisteredObjectBsonSerializer()
    {
        var registry = BsonSerializer.SerializerRegistry;
        var cacheField = registry.GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(f => f.FieldType == typeof(ConcurrentDictionary<Type, IBsonSerializer>));
        var cache = (ConcurrentDictionary<Type, IBsonSerializer>)cacheField.GetValue(registry)!;
        cache.TryRemove(typeof(object), out _);
    }
}
