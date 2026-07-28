using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using System.Reflection;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMqServiceCoverageTests
{
    [Fact]
    public void RabbitMqChannel_ShouldReturnChannel_WhenInitialized()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        var service = new RabbitMqService(logger.Object, CreateConfig("amqp://guest:guest@localhost:5672"));

        SetPrivateField(service, "_channel", channel.Object);

        Assert.Same(channel.Object, service.RabbitMqChannel);
    }

    [Fact]
    public async Task CreateConnectionAsync_ShouldLogError_WhenConnectionStringIsInvalid()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var service = new RabbitMqService(logger.Object, CreateConfig("not-a-valid-uri"));

        var exception = await Record.ExceptionAsync(() => service.CreateConnectionAsync());

        Assert.Null(exception);
        Assert.Throws<InvalidOperationException>(() => _ = service.RabbitMqChannel);
    }

    [Fact]
    public async Task CreateConnectionAsync_ShouldLogError_WhenBrokerIsUnreachable()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var service = new RabbitMqService(logger.Object, CreateConfig("amqp://guest:guest@127.0.0.1:1"));

        var exception = await Record.ExceptionAsync(() => service.CreateConnectionAsync());

        Assert.Null(exception);
        Assert.Throws<InvalidOperationException>(() => _ = service.RabbitMqChannel);
    }

    private static MessageConfiguration CreateConfig(string connection)
    {
        return new MessageConfiguration
        {
            Connection = connection,
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
