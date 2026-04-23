using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMessageClientAdditionalTests
{
    [Fact]
    public async Task SendToConsumerAsync_ShouldAttachBasicReturnHandler_AndLogWarning_WhenMessageIsReturned()
    {
        // Covers the BasicReturnAsync handler attached in InitializeRabbitMqAsync
        // (RabbitMessageClient.cs:66-72). We capture the attached handler via
        // Moq's SetupAdd, run a normal publish (which triggers initialization),
        // then invoke the captured handler with a synthetic event to exercise
        // its body and assert the logger received a Warning.
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageClient>>();
        // Source-generated logging is gated by IsEnabled (Moq default false).
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        AsyncEventHandler<BasicReturnEventArgs>? captured = null;

        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.SetupAdd(x => x.BasicReturnAsync += It.IsAny<AsyncEventHandler<BasicReturnEventArgs>>())
               .Callback<AsyncEventHandler<BasicReturnEventArgs>>(h => captured = h);
        channel.Setup(x => x.BasicPublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);

        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client-additional"));

        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "orders.queue",
            Payload = new TestPayload { Value = "ok" },
            Context = string.Empty
        });

        Assert.NotNull(captured);

        var ea = (BasicReturnEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(BasicReturnEventArgs));
        SetMember(ea, "ReplyCode", (ushort)312);
        SetMember(ea, "ReplyText", "NO_ROUTE");
        SetMember(ea, "Exchange", "orders.exchange");
        SetMember(ea, "RoutingKey", "orders.key");
        SetMember(ea, "Body", new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("returned-body")));

        await captured!.Invoke(new object(), ea);

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldThrow_WhenRabbitServiceReturnsNullChannel()
    {
        // Covers RabbitMessageClient.InitializeRabbitMqAsync L61-64:
        // if the service returns a null channel, EnsureInitializedAsync surfaces
        // InvalidOperationException.
        var logger = new Mock<ILogger<RabbitMessageClient>>();
        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns((IChannel)null!);

        var client = new RabbitMessageClient(
            logger.Object,
            rabbitService.Object,
            CreateMessageConfiguration(),
            new ActivitySource("test-rabbit-client-additional"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
            {
                ConsumerName = "orders.queue",
                Payload = new TestPayload { Value = "x" },
                Context = string.Empty
            }));
    }

    private static MessageConfiguration CreateMessageConfiguration() => new()
    {
        Connection = "amqp://guest:guest@localhost:5672",
        RabbitMqConfiguration = new RabbitMqConfiguration { MessageTtlSeconds = 30 }
    };

    private sealed class TestPayload
    {
        public string? Value { get; set; }
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

    private static void SetMember(object instance, string name, object value)
    {
        var type = instance.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property?.SetMethod != null)
        {
            property.SetValue(instance, value);
            return;
        }

        var field = type.GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        field!.SetValue(instance, value);
    }
}
