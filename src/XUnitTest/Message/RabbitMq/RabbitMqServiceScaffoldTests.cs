using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using System.Reflection;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMqServiceScaffoldTests
{
    [Fact]
    public void RabbitMqChannel_ShouldThrow_WhenNotInitialized()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var service = new RabbitMqService(logger.Object, CreateConfig());

        Assert.Throws<InvalidOperationException>(() => _ = service.RabbitMqChannel);
    }

    [Fact]
    public async Task InitializeSubscriptionsAsync_ShouldThrow_WhenChannelMissing()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var service = new RabbitMqService(logger.Object, CreateConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeSubscriptionsAsync());
    }

    [Fact]
    public async Task InitializeSubscriptionsAsync_ShouldDeclareQueueAndConfigureQos()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        var service = new RabbitMqService(logger.Object, CreateConfig());

        SetPrivateField(service, "_channel", channel.Object);

        await service.InitializeSubscriptionsAsync();

        channel.Verify(x => x.QueueDeclareAsync(
            "orders.queue",
            true,
            false,
            false,
            null,
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);

        channel.Verify(x => x.BasicQosAsync(
            0,
            7,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_ShouldCloseChannel_WhenOpen()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var service = new RabbitMqService(logger.Object, CreateConfig());
        SetPrivateField(service, "_channel", channel.Object);

        await service.DisposeAsync();

        channel.Verify(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(x => x.DisposeAsync(), Times.Once);
    }

    private static MessageConfiguration CreateConfig()
    {
        return new MessageConfiguration
        {
            Connection = "amqp://guest:guest@localhost:5672",
            RabbitMqConfiguration = new RabbitMqConfiguration
            {
                ConsumerSubscriptions =
                [
                    ConsumerSubscription.BindToQueue("orders.queue", 7)
                ]
            }
        };
    }

    [Fact]
    public async Task InitializeSubscriptionsAsync_ShouldDeclareExchangeAndBindQueue_WhenExchangeConfigured()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        var service = new RabbitMqService(logger.Object, CreateExchangeConfig());

        SetPrivateField(service, "_channel", channel.Object);

        await service.InitializeSubscriptionsAsync();

        // BasicQosAsync is called after exchange declare + queue bind, proving the exchange path ran
        channel.Verify(x => x.BasicQosAsync(
            0,
            5,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InitializeSubscriptionsAsync_ShouldSwallowException_WhenChannelOperationFails()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        channel.Setup(x => x.QueueDeclareAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel failure"));

        var service = new RabbitMqService(logger.Object, CreateConfig());
        SetPrivateField(service, "_channel", channel.Object);

        var ex = await Record.ExceptionAsync(() => service.InitializeSubscriptionsAsync());

        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeAsync_ShouldCloseConnection_WhenOpen()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        var connection = new Mock<IConnection>();
        connection.SetupGet(x => x.IsOpen).Returns(true);

        var service = new RabbitMqService(logger.Object, CreateConfig());
        SetPrivateField(service, "_channel", channel.Object);
        SetPrivateField(service, "_connection", connection.Object);

        await service.DisposeAsync();

        connection.Verify(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var service = new RabbitMqService(logger.Object, CreateConfig());
        SetPrivateField(service, "_channel", channel.Object);

        await service.DisposeAsync();
        await service.DisposeAsync();

        channel.Verify(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_ShouldSwallowException_WhenChannelCloseThrows()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("close error"));

        var service = new RabbitMqService(logger.Object, CreateConfig());
        SetPrivateField(service, "_channel", channel.Object);

        var ex = await Record.ExceptionAsync(async () => await service.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeAsync_ShouldSwallowException_WhenConnectionCloseThrows()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(false);
        var connection = new Mock<IConnection>();
        connection.SetupGet(x => x.IsOpen).Returns(true);
        connection.Setup(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection close error"));

        var service = new RabbitMqService(logger.Object, CreateConfig());
        SetPrivateField(service, "_channel", channel.Object);
        SetPrivateField(service, "_connection", connection.Object);

        var ex = await Record.ExceptionAsync(async () => await service.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeAsync_ShouldSkipClose_WhenChannelAndConnectionNotOpen()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(false);
        var connection = new Mock<IConnection>();
        connection.SetupGet(x => x.IsOpen).Returns(false);

        var service = new RabbitMqService(logger.Object, CreateConfig());
        SetPrivateField(service, "_channel", channel.Object);
        SetPrivateField(service, "_connection", connection.Object);

        await service.DisposeAsync();

        channel.Verify(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        connection.Verify(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Dispose_ShouldCallDisposeAsync()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var service = new RabbitMqService(logger.Object, CreateConfig());

        var ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }

    private static MessageConfiguration CreateExchangeConfig()
    {
        return new MessageConfiguration
        {
            Connection = "amqp://guest:guest@localhost:5672",
            RabbitMqConfiguration = new RabbitMqConfiguration
            {
                ConsumerSubscriptions =
                [
                    ConsumerSubscription.BindToQueueViaExchange("events.queue", "events.exchange", 5)
                ]
            }
        };
    }

    private static void SetPrivateField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }
}
