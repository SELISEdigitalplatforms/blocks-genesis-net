using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;

namespace XUnitTest.Message;

public class RoutingTableTests
{
    [Fact]
    public void Constructor_ShouldBuildRoute_ForRegisteredConsumer()
    {
        var services = new ServiceCollection();
        services.AddTransient<IConsumer<TestMessage>, TestMessageConsumer>();

        var routingTable = new RoutingTable(services);

        Assert.Single(routingTable.Routes);
        Assert.True(routingTable.Routes.ContainsKey(nameof(TestMessage)));
        Assert.Equal(typeof(IConsumer<TestMessage>), routingTable.Routes[nameof(TestMessage)].ConsumerType);
    }

    [Fact]
    public void Constructor_ShouldIgnoreFactoryRegistrations_WithoutImplementationType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConsumer<TestMessage>>(_ => new TestMessageConsumer());

        var routingTable = new RoutingTable(services);

        Assert.Empty(routingTable.Routes);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenMultipleConsumersHandleSameMessageType()
    {
        var services = new ServiceCollection();
        services.AddTransient<IConsumer<TestMessage>, TestMessageConsumer>();
        services.AddTransient<IConsumer<TestMessage>, DuplicateTestMessageConsumer>();

        var ex = Assert.Throws<InvalidOperationException>(() => new RoutingTable(services));

        Assert.Contains("Message of type", ex.Message);
        Assert.Contains(nameof(TestMessage), ex.Message);
    }

    private sealed class TestMessage;

    private sealed class TestMessageConsumer : IConsumer<TestMessage>
    {
        public Task Consume(TestMessage context) => Task.CompletedTask;
    }

    private sealed class DuplicateTestMessageConsumer : IConsumer<TestMessage>
    {
        public Task Consume(TestMessage context) => Task.CompletedTask;
    }
}
