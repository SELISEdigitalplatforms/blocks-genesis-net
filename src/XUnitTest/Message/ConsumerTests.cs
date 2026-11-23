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

    [Fact]
    public async Task ProcessMessageAsync_exposes_the_queue_to_the_consumer_and_clears_it_after()
    {
        QueueRecordingConsumer.Reset();
        var consumer = BuildQueueRecordingConsumer();

        await consumer.ProcessMessageAsync(nameof(TestMessage), "{\"Value\":\"ok\"}", "blocks_os_action_webhook_listener");

        Assert.Equal("blocks_os_action_webhook_listener", QueueRecordingConsumer.RecordedQueue);
        Assert.Null(MessageContext.Queue);
    }

    [Fact]
    public async Task ProcessMessageAsync_clears_the_queue_when_the_consumer_throws()
    {
        QueueRecordingConsumer.Reset();
        QueueRecordingConsumer.Throw = true;
        try
        {
            var consumer = BuildQueueRecordingConsumer();

            await consumer.ProcessMessageAsync(nameof(TestMessage), "{\"Value\":\"ok\"}", "some_queue");

            Assert.Equal("some_queue", QueueRecordingConsumer.RecordedQueue);
            Assert.Null(MessageContext.Queue);
        }
        finally
        {
            QueueRecordingConsumer.Throw = false;
        }
    }

    [Fact]
    public async Task Two_overload_passes_no_queue()
    {
        QueueRecordingConsumer.Reset();
        var consumer = BuildQueueRecordingConsumer();

        await consumer.ProcessMessageAsync(nameof(TestMessage), "{\"Value\":\"ok\"}");

        Assert.True(QueueRecordingConsumer.Invoked);
        Assert.Null(QueueRecordingConsumer.RecordedQueue);
    }

    private static Consumer BuildQueueRecordingConsumer()
    {
        var table = new RoutingTable(new ServiceCollection());
        var method = typeof(QueueRecordingConsumer).GetMethod(nameof(QueueRecordingConsumer.Consume), BindingFlags.Public | BindingFlags.Instance)!;
        table.Routes[nameof(TestMessage)] = new RoutingInfo(nameof(TestMessage), typeof(TestMessage), typeof(IConsumer<TestMessage>), method);
        var serviceProvider = new ServiceCollection()
            .AddSingleton(typeof(IConsumer<TestMessage>), new QueueRecordingConsumer())
            .BuildServiceProvider();
        return new Consumer(new Mock<ILogger<Consumer>>().Object, serviceProvider, table);
    }

    private sealed class QueueRecordingConsumer : IConsumer<TestMessage>
    {
        public static bool Invoked { get; private set; }
        public static string? RecordedQueue { get; private set; }
        public static bool Throw { get; set; }

        public static void Reset()
        {
            Invoked = false;
            RecordedQueue = null;
        }

        public Task Consume(TestMessage context)
        {
            Invoked = true;
            RecordedQueue = MessageContext.Queue;
            if (Throw) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }
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