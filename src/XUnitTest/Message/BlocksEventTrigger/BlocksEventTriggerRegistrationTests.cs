using Blocks.Genesis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using XUnitTest.Delegation;

namespace XUnitTest.Message.BlocksEventTrigger;

// Shares the collection of the other ConfigureServices tests: both mutate ApplicationConfigurations' static secrets.
[Collection("DirectorySensitiveTests")]
public sealed class BlocksEventTriggerRegistrationTests
{
    [Fact]
    public void H12_ConfigureServicesRegistersTheForwarderAndTheCache()
    {
        var previousSecret = GetStatic("_blocksSecret");
        var previousServiceName = GetStatic("_serviceName");
        var previousSwagger = GetStatic("_blocksSwaggerOptions");
        try
        {
            SetStatic("_serviceName", "svc-event-trigger-di");
            SetStatic("_blocksSwaggerOptions", null);
            SetStatic("_blocksSecret", new BlocksSecret
            {
                DatabaseConnectionString = "mongodb://127.0.0.1:27017/genesis-event-trigger-di",
                CacheConnectionString = "localhost:6379,abortConnect=false",
                MessageConnectionString = string.Empty,
                TraceConnectionString = string.Empty
            });
            RemoveRegisteredObjectBsonSerializer();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

            ApplicationConfigurations.ConfigureServices(services, new MessageConfiguration { ServiceName = "svc-event-trigger-di" });

            AssertSingleton<IBlocksEventTriggerForwarder, BlocksEventTriggerForwarder>(services);
            AssertSingleton<IBlocksEventCatalogCache, BlocksEventCatalogCache>(services);
            Assert.Contains(services, d => d.ServiceType == typeof(TimeProvider));
        }
        finally
        {
            SetStatic("_blocksSecret", previousSecret);
            SetStatic("_serviceName", previousServiceName);
            SetStatic("_blocksSwaggerOptions", previousSwagger);
        }
    }

    [Theory]
    [InlineData("rabbit")]
    [InlineData("azure")]
    public void H12_IMessageClientResolvedFromTheRealConfigureServicesUsesTheForwardingConstructor(string transport)
    {
        var previousSecret = GetStatic("_blocksSecret");
        var previousServiceName = GetStatic("_serviceName");
        var previousSwagger = GetStatic("_blocksSwaggerOptions");
        try
        {
            SetStatic("_serviceName", "svc-event-trigger-di-resolve");
            SetStatic("_blocksSwaggerOptions", null);
            SetStatic("_blocksSecret", new BlocksSecret
            {
                DatabaseConnectionString = "mongodb://127.0.0.1:27017/genesis-event-trigger-di",
                RootDatabaseName = "genesis-event-trigger-di-root",
                CacheConnectionString = "localhost:6379,abortConnect=false",
                MessageConnectionString = string.Empty,
                TraceConnectionString = string.Empty
            });
            RemoveRegisteredObjectBsonSerializer();

            // No broker is contacted: the RabbitMQ topology step returns early without a connection string, and
            // the Azure one has no queues or topics to create. Resolving builds the whole real dependency graph.
            var messageConfiguration = transport == "azure"
                ? new MessageConfiguration
                {
                    ServiceName = "svc-event-trigger-di-resolve",
                    Connection = TriggerKit.ValidAzureConnection,
                    AzureServiceBusConfiguration = new AzureServiceBusConfiguration { Queues = [], Topics = [] }
                }
                : new MessageConfiguration
                {
                    ServiceName = "svc-event-trigger-di-resolve",
                    RabbitMqConfiguration = new RabbitMqConfiguration()
                };

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            ApplicationConfigurations.ConfigureServices(services, messageConfiguration);

            using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IMessageClient>();

            Assert.IsType(transport == "azure" ? typeof(AzureMessageClient) : typeof(RabbitMessageClient), client);
            Assert.Same(provider.GetRequiredService<IBlocksEventTriggerForwarder>(), Forwarder(client));
            Assert.IsType<BlocksEventTriggerForwarder>(Forwarder(client));
            // The client's own logger (for a throwing forwarder) is resolved too, not the null fallback.
            var logger = client.GetType().GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);
            Assert.IsNotType<Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureMessageClient>>(logger);
            Assert.IsNotType<Microsoft.Extensions.Logging.Abstractions.NullLogger<RabbitMessageClient>>(logger);
        }
        finally
        {
            SetStatic("_blocksSecret", previousSecret);
            SetStatic("_serviceName", previousServiceName);
            SetStatic("_blocksSwaggerOptions", previousSwagger);
        }
    }

    [Fact]
    public void H12_AddBlocksEventTriggerResolvesOneForwarderPerProcess()
    {
        var services = BaseServices();

        services.AddBlocksEventTrigger();
        services.AddBlocksEventTrigger();

        using var provider = services.BuildServiceProvider();
        var forwarder = provider.GetRequiredService<IBlocksEventTriggerForwarder>();
        Assert.IsType<BlocksEventTriggerForwarder>(forwarder);
        Assert.Same(forwarder, provider.GetRequiredService<IBlocksEventTriggerForwarder>());
        Assert.Single(services, d => d.ServiceType == typeof(IBlocksEventTriggerForwarder));
    }

    [Fact]
    public void H12_AddBlocksEventTriggerKeepsAnExistingTimeProvider()
    {
        var services = BaseServices();
        var clock = new TriggerClock();
        services.AddSingleton<TimeProvider>(clock);

        services.AddBlocksEventTrigger();

        using var provider = services.BuildServiceProvider();
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void H12_DependencyInjectionPicksTheForwardingConstructor_RabbitMq(bool registerTrigger)
    {
        var services = BaseServices();
        services.AddSingleton(Mock.Of<IRabbitMqService>());
        services.AddSingleton<IMessageClient, RabbitMessageClient>();
        if (registerTrigger)
        {
            services.AddBlocksEventTrigger();
        }

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IMessageClient>();

        Assert.Equal(registerTrigger, Forwarder(client) is not null);
    }

    [Fact]
    public void H12_DependencyInjectionPicksTheForwardingConstructor_Azure()
    {
        var services = BaseServices();
        services.AddSingleton(new MessageConfiguration
        {
            Connection = TriggerKit.ValidAzureConnection,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        });
        services.AddSingleton<IMessageClient, AzureMessageClient>();
        services.AddBlocksEventTrigger();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<BlocksEventTriggerForwarder>(Forwarder(provider.GetRequiredService<IMessageClient>()));
    }

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IKeyValueStore>());
        services.AddSingleton(Mock.Of<ICacheClient>());
        services.AddSingleton(new MessageConfiguration { ServiceName = TriggerKit.ServiceName, RabbitMqConfiguration = new RabbitMqConfiguration() });
        services.AddSingleton(new ActivitySource("bet-di"));
        services.AddSingleton(DelegationTestDoubles.NoGrantFactory());
        return services;
    }

    private static object? Forwarder(IMessageClient client) =>
        client.GetType().GetField("_forwarder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);

    private static void AssertSingleton<TService, TImplementation>(IServiceCollection services)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
    }

    private static object? GetStatic(string name) =>
        typeof(ApplicationConfigurations).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    private static void SetStatic(string name, object? value) =>
        typeof(ApplicationConfigurations).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

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
