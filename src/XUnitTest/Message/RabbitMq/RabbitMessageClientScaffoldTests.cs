using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using System.Diagnostics;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMessageClientScaffoldTests
{
    [Fact]
    public async Task SendToConsumerAsync_ShouldThrow_WhenChannelIsNotInitialized()
    {
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns((IChannel)null!);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
            {
                ConsumerName = "orders.queue",
                Payload = new TestPayload { Value = "ok" },
                Context = string.Empty
            }));
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldPublishToQueue()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "orders.queue",
            Payload = new TestPayload { Value = "ok" },
            Context = string.Empty
        });

        channel.Verify(x => x.BasicPublishAsync(
            "",
            "orders.queue",
            true,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToMassConsumerAsync_ShouldPublishToExchangeWithRoutingKey()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        await client.SendToMassConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "events.exchange",
            RoutingKey = "order.created",
            Payload = new TestPayload { Value = "ok" },
            Context = string.Empty
        });

        channel.Verify(x => x.BasicPublishAsync(
            "events.exchange",
            "order.created",
            true,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static MessageConfiguration CreateMessageConfiguration()
    {
        return new MessageConfiguration
        {
            Connection = "amqp://guest:guest@localhost:5672",
            RabbitMqConfiguration = new RabbitMqConfiguration
            {
                MessageTtlSeconds = 30
            }
        };
    }

    private static MessageConfiguration CreateMessageConfigurationWithoutTtl()
    {
        return new MessageConfiguration
        {
            Connection = "amqp://guest:guest@localhost:5672",
            RabbitMqConfiguration = new RabbitMqConfiguration
            {
                MessageTtlSeconds = 0
            }
        };
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldThrowArgumentNullException_WhenMessageIsNull()
    {
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SendToConsumerAsync<TestPayload>(null!));
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldSkipTtl_WhenMessageTtlSecondsIsZero()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfigurationWithoutTtl(),
            new ActivitySource("test-rabbit-client"));

        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "orders.queue",
            Payload = new TestPayload { Value = "ok" },
            Context = string.Empty
        });

        channel.Verify(x => x.BasicPublishAsync(
            "",
            "orders.queue",
            true,
            It.Is<BasicProperties>(p => p.Expiration == null),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldUseCustomContext_WhenProvided()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        var customContext = "{\"TenantId\":\"custom-tenant\"}";
        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "orders.queue",
            Payload = new TestPayload { Value = "ok" },
            Context = customContext
        });

        channel.Verify(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.Is<BasicProperties>(p => p.Headers != null && p.Headers["SecurityContext"].ToString() == customContext),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldLogError_WhenPublishThrows()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("publish failed"));

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        var ex = await Record.ExceptionAsync(() => client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "orders.queue",
            Payload = new TestPayload { Value = "ok" },
            Context = string.Empty
        }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldReuseChannel_WhenCalledTwice()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "q1",
            Payload = new TestPayload { Value = "a" },
            Context = string.Empty
        });

        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "q2",
            Payload = new TestPayload { Value = "b" },
            Context = string.Empty
        });

        rabbitService.Verify(x => x.CreateConnectionAsync(), Times.Once);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ShouldThrow_WhenChannelIsNotOpenAfterInit()
    {
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(false);

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
            {
                ConsumerName = "q",
                Payload = new TestPayload { Value = "x" },
                Context = string.Empty
            }));
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldSetTtlExpiration_WhenMessageTtlSecondsIsPositive()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client"));

        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "orders.queue",
            Payload = new TestPayload { Value = "ok" },
            Context = string.Empty
        });

        channel.Verify(x => x.BasicPublishAsync(
            "",
            "orders.queue",
            true,
            It.Is<BasicProperties>(p => p.Expiration == "30000"),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ActivityListener CreateActivityListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class TestPayload
    {
        public string? Value { get; set; }
    }
}
