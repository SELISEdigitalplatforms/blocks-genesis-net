using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System.Reflection;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMqServiceCoverageTests
{
    [Fact]
    public void RabbitMqChannel_ShouldReturnChannel_WhenInitialized()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var channel = new Mock<IChannel>();
        var service = new RabbitMqService(logger.Object, CreateConfig("amqp://<username>:<password>@localhost:5672"));

        SetPrivateField(service, "_channel", channel.Object);

        Assert.Same(channel.Object, service.RabbitMqChannel);
    }

    // I01-WP1: CreateConnectionAsync no longer swallows a failed connect. It retries while the broker is
    // unreachable and then throws, so the channel is never used uninitialised.
    [Fact]
    public async Task CreateConnectionAsync_ShouldThrowWithoutRetrying_WhenConnectionStringIsInvalid()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var service = new RabbitMqService(logger.Object, CreateConfig("not-a-valid-uri")) { DependencyWait = TimeSpan.FromSeconds(30) };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<UriFormatException>(() => service.CreateConnectionAsync());

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "an invalid connection string must not be retried");
        Assert.Throws<InvalidOperationException>(() => _ = service.RabbitMqChannel);
    }

    [Fact]
    public async Task CreateConnectionAsync_ShouldRetryThenThrow_WhenBrokerIsUnreachable()
    {
        var logger = new Mock<ILogger<RabbitMqService>>();
        var service = new RabbitMqService(logger.Object, CreateConfig("amqp://<username>:<password>@127.0.0.1:1")) { DependencyWait = TimeSpan.FromSeconds(3) };

        await Assert.ThrowsAsync<BrokerUnreachableException>(() => service.CreateConnectionAsync());

        Assert.Throws<InvalidOperationException>(() => _ = service.RabbitMqChannel);
        // A failed attempt that is followed by a retry writes one warning (the connect itself can take a few seconds).
        logger.Verify(x => x.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce());
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
