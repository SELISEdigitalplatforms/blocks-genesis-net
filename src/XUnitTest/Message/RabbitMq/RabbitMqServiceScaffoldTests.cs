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

    private static void SetPrivateField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }
}
