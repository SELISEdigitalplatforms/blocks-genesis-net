using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Reflection;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMessageClientCoverageTests
{
    [Fact]
    public async Task SendToConsumerAsync_ShouldSkipInitialization_WhenChannelIsAlreadyOpen()
    {
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = CreatePublishableChannel();

        var client = CreateClient(logger.Object, rabbitService.Object, CreateConfiguration());
        SetPrivateField(client, "_channel", channel.Object);

        await client.SendToConsumerAsync(new ConsumerMessage<CoveragePayload>
        {
            ConsumerName = "orders.queue",
            Payload = new CoveragePayload { Value = "pre-set" },
            Context = string.Empty
        });

        rabbitService.Verify(x => x.CreateConnectionAsync(), Times.Never);
        channel.Verify(x => x.BasicPublishAsync(
            "",
            "orders.queue",
            true,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldThrow_WhenInitializationCompletedWithoutChannel()
    {
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();

        var client = CreateClient(logger.Object, rabbitService.Object, CreateConfiguration());
        SetPrivateField(client, "_initializationTask", Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendToConsumerAsync(new ConsumerMessage<CoveragePayload>
            {
                ConsumerName = "orders.queue",
                Payload = new CoveragePayload { Value = "never-sent" },
                Context = string.Empty
            }));

        rabbitService.Verify(x => x.CreateConnectionAsync(), Times.Never);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldPublish_WithAmbientActivityBaggageAndSecurityContext()
    {
        using var listener = CreateActivityListener();
        using var source = new ActivitySource("test-rabbit-client-coverage-ambient");
        using var ambient = source.StartActivity("ambient-send");
        Assert.NotNull(ambient);
        ambient!.AddBaggage("cov-baggage-key", "cov-baggage-value");

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = CreatePublishableChannel();

        var client = CreateClient(logger.Object, rabbitService.Object, CreateConfiguration());
        SetPrivateField(client, "_channel", channel.Object);

        BlocksContext.SetContext(BlocksContext.Create("tenant-cov", [], "user-cov", true, "", "", DateTime.MinValue, "", [], "", "", "", "", "tenant-cov"));
        try
        {
            await client.SendToConsumerAsync(new ConsumerMessage<CoveragePayload>
            {
                ConsumerName = "orders.queue",
                Payload = new CoveragePayload { Value = "context" },
                Context = string.Empty,
                RoutingKey = "ignored.on.queue.sends"
            });
        }
        finally
        {
            BlocksContext.ClearContext();
        }

        channel.Verify(x => x.BasicPublishAsync(
            "",
            "orders.queue",
            true,
            It.Is<BasicProperties>(p => p.Headers != null && p.Headers["TenantId"]!.ToString() == "tenant-cov"),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldPublish_WhenMessageConfigurationIsNull()
    {
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = CreatePublishableChannel();

        var client = CreateClient(logger.Object, rabbitService.Object, null!);
        SetPrivateField(client, "_channel", channel.Object);

        await client.SendToConsumerAsync(new ConsumerMessage<CoveragePayload>
        {
            ConsumerName = "orders.queue",
            Payload = new CoveragePayload { Value = "no-config" },
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
    public async Task SendToConsumerAsync_ShouldPublish_WhenRabbitMqConfigurationIsNull()
    {
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = CreatePublishableChannel();

        var client = CreateClient(logger.Object, rabbitService.Object, new MessageConfiguration
        {
            Connection = "amqp://guest:guest@localhost:5672",
            RabbitMqConfiguration = null
        });
        SetPrivateField(client, "_channel", channel.Object);

        await client.SendToConsumerAsync(new ConsumerMessage<CoveragePayload>
        {
            ConsumerName = "orders.queue",
            Payload = new CoveragePayload { Value = "no-rabbit-config" },
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
    public async Task SendToMassConsumerAsync_ShouldUseEmptyRoutingKey_WhenRoutingKeyIsNull()
    {
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = CreatePublishableChannel();

        var client = CreateClient(logger.Object, rabbitService.Object, CreateConfiguration());
        SetPrivateField(client, "_channel", channel.Object);

        await client.SendToMassConsumerAsync(new ConsumerMessage<CoveragePayload>
        {
            ConsumerName = "events.exchange",
            Payload = new CoveragePayload { Value = "broadcast" },
            Context = string.Empty,
            RoutingKey = null!
        });

        channel.Verify(x => x.BasicPublishAsync(
            "events.exchange",
            "",
            true,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldTagEmptyRoutingKey_WhenRoutingKeyIsNullAndListenerIsActive()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = CreatePublishableChannel();

        var client = CreateClient(logger.Object, rabbitService.Object, CreateConfiguration());
        SetPrivateField(client, "_channel", channel.Object);

        await client.SendToConsumerAsync(new ConsumerMessage<CoveragePayload>
        {
            ConsumerName = "orders.queue",
            Payload = new CoveragePayload { Value = "null-routing-key" },
            Context = string.Empty,
            RoutingKey = null!
        });

        channel.Verify(x => x.BasicPublishAsync(
            "",
            "orders.queue",
            true,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RabbitMessageClient CreateClient(
        ILogger<RabbitMessageClient> logger,
        IRabbitMqService rabbitService,
        MessageConfiguration configuration)
    {
        return new RabbitMessageClient(
            logger,
            rabbitService,
            configuration,
            new ActivitySource("test-rabbit-client-coverage"));
    }

    private static MessageConfiguration CreateConfiguration()
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

    private static Mock<IChannel> CreatePublishableChannel()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        return channel;
    }

    private static void SetPrivateField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
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

    private sealed class CoveragePayload
    {
        public string? Value { get; set; }
    }
}
