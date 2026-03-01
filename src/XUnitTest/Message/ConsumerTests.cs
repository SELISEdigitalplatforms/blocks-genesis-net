using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;

namespace XUnitTest.Message;

public class ConsumerTests
{
    [Fact]
    public async Task ProcessMessageAsync_ShouldDoNothing_WhenRouteDoesNotExist()
    {
        var logger = new Mock<ILogger<Consumer>>();
        var routingTable = new RoutingTable(new ServiceCollection());
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var consumer = new Consumer(logger.Object, serviceProvider, routingTable);

        await consumer.ProcessMessageAsync("UnknownMessage", "{}");

        Assert.False(TestConsumer.Invoked);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldDoNothing_WhenPayloadCannotDeserialize()
    {
        TestConsumer.Invoked = false;

        var logger = new Mock<ILogger<Consumer>>();
        var routingTable = BuildRoutingTableForTestMessage();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(typeof(IConsumer<TestMessage>), new TestConsumer())
            .BuildServiceProvider();

        var consumer = new Consumer(logger.Object, serviceProvider, routingTable);

        await consumer.ProcessMessageAsync(nameof(TestMessage), "{ invalid-json");

        Assert.False(TestConsumer.Invoked);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldInvokeRegisteredConsumer_WhenRouteAndPayloadAreValid()
    {
        TestConsumer.Invoked = false;

        var logger = new Mock<ILogger<Consumer>>();
        var routingTable = BuildRoutingTableForTestMessage();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(typeof(IConsumer<TestMessage>), new TestConsumer())
            .BuildServiceProvider();

        var consumer = new Consumer(logger.Object, serviceProvider, routingTable);

        await consumer.ProcessMessageAsync(nameof(TestMessage), "{\"Value\":\"ok\"}");

        Assert.True(TestConsumer.Invoked);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldNotInvoke_WhenServiceProviderCannotResolveConsumer()
    {
        TestConsumer.Invoked = false;

        var logger = new Mock<ILogger<Consumer>>();
        var routingTable = BuildRoutingTableForTestMessage();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var consumer = new Consumer(logger.Object, serviceProvider, routingTable);

        await consumer.ProcessMessageAsync(nameof(TestMessage), "{\"Value\":\"ok\"}");

        Assert.False(TestConsumer.Invoked);
    }

    private static RoutingTable BuildRoutingTableForTestMessage()
    {
        var table = new RoutingTable(new ServiceCollection());
        var method = typeof(TestConsumer).GetMethod(nameof(TestConsumer.Consume), BindingFlags.Public | BindingFlags.Instance)!;
        table.Routes[nameof(TestMessage)] = new RoutingInfo(nameof(TestMessage), typeof(TestMessage), typeof(IConsumer<TestMessage>), method);
        return table;
    }

    private sealed class TestMessage
    {
        public string? Value { get; set; }
    }

    private sealed class TestConsumer : IConsumer<TestMessage>
    {
        public static bool Invoked { get; set; }

        public Task Consume(TestMessage context)
        {
            Invoked = true;
            return Task.CompletedTask;
        }
    }
}