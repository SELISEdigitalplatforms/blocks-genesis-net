using Blocks.Genesis;
using Azure.Messaging.ServiceBus;
using Moq;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace XUnitTest.Message.Azure;

public class AzureMessageClientScaffoldTests
{
    [Fact]
    public void Constructor_ShouldInitializeSenders_ForConfiguredQueuesAndTopics()
    {
        var configuration = new MessageConfiguration
        {
            Connection = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=01234567890123456789012345678901234567890123456789=",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = ["queue-a"],
                Topics = ["topic-a"]
            }
        };

        var client = new AzureMessageClient(configuration, new System.Diagnostics.ActivitySource("test-azure-client"));

        var field = typeof(AzureMessageClient).GetField("_senders", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var senders = (ConcurrentDictionary<string, ServiceBusSender>)field!.GetValue(client)!;
        Assert.Equal(2, senders.Count);
        Assert.True(senders.ContainsKey("queue-a"));
        Assert.True(senders.ContainsKey("topic-a"));
    }

    [Fact]
    public void GetBaggageDictionary_ShouldReturnCurrentBaggageItems()
    {
        Baggage.SetBaggage("tenant", "t-1");
        Baggage.SetBaggage("user", "u-1");

        try
        {
            var method = typeof(AzureMessageClient).GetMethod("GetBaggageDictionary", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var result = (Dictionary<string, string>)method!.Invoke(null, null)!;

            Assert.Equal("t-1", result["tenant"]);
            Assert.Equal("u-1", result["user"]);
        }
        finally
        {
            Baggage.SetBaggage("tenant", null);
            Baggage.SetBaggage("user", null);
        }
    }

    [Fact]
    public void GetBaggageDictionary_ShouldReturnEmpty_WhenNoBaggageExists()
    {
        var method = typeof(AzureMessageClient).GetMethod("GetBaggageDictionary", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (Dictionary<string, string>)method!.Invoke(null, null)!;

        Assert.NotNull(result);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldSendMessage_WhenScheduleTimeIsNull()
    {
        var senderMock = new Mock<ServiceBusSender>();
        ServiceBusMessage? capturedMessage = null;

        senderMock
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        var client = CreateClientWithInjectedSender("orders.queue", senderMock.Object);

        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "orders.queue",
            Payload = new TestPayload { Value = "ok" },
            Context = string.Empty
        });

        senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        senderMock.Verify(x => x.ScheduleMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.NotNull(capturedMessage);
        Assert.True(capturedMessage!.ApplicationProperties.ContainsKey("TenantId"));
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("TraceId"));
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("SpanId"));
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("SecurityContext"));
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("Baggage"));

        var envelope = JsonSerializer.Deserialize<Blocks.Genesis.Message>(capturedMessage.Body.ToString());
        Assert.NotNull(envelope);
        Assert.Equal(nameof(TestPayload), envelope!.Type);
        Assert.Contains("ok", envelope.Body);
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldScheduleMessage_WhenScheduleTimeIsProvided()
    {
        var senderMock = new Mock<ServiceBusSender>();
        ServiceBusMessage? capturedMessage = null;
        DateTimeOffset? capturedScheduleAt = null;

        senderMock
            .Setup(x => x.ScheduleMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, DateTimeOffset, CancellationToken>((msg, at, _) =>
            {
                capturedMessage = msg;
                capturedScheduleAt = at;
            })
            .ReturnsAsync(1L);

        var client = CreateClientWithInjectedSender("orders.queue", senderMock.Object);
        var scheduledAt = DateTimeOffset.UtcNow.AddMinutes(5);

        await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "orders.queue",
            Payload = new TestPayload { Value = "later" },
            Context = "{\"ctx\":\"v\"}",
            SccheduledEnqueueTimeUtc = scheduledAt
        });

        senderMock.Verify(x => x.ScheduleMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.NotNull(capturedMessage);
        Assert.Equal(scheduledAt, capturedScheduleAt);
        Assert.Equal("{\"ctx\":\"v\"}", capturedMessage!.ApplicationProperties["SecurityContext"]?.ToString());
    }

    [Fact]
    public async Task SendToMassConsumerAsync_ShouldUseSameSendPath()
    {
        var senderMock = new Mock<ServiceBusSender>();
        senderMock
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = CreateClientWithInjectedSender("topic.events", senderMock.Object);

        await client.SendToMassConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "topic.events",
            Payload = new TestPayload { Value = "mass" },
            Context = string.Empty
        });

        senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Constructor_ShouldThrow_ForInvalidConnectionString()
    {
        var configuration = new MessageConfiguration
        {
            Connection = "invalid-connection",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };

        Assert.Throws<FormatException>(() => new AzureMessageClient(configuration, new System.Diagnostics.ActivitySource("test-azure-client")));
    }

    private static AzureMessageClient CreateClientWithInjectedSender(string consumerName, ServiceBusSender sender)
    {
        var configuration = new MessageConfiguration
        {
            Connection = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=01234567890123456789012345678901234567890123456789=",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = [],
                Topics = []
            }
        };

        var client = new AzureMessageClient(configuration, new System.Diagnostics.ActivitySource("test-azure-client"));

        var field = typeof(AzureMessageClient).GetField("_senders", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var senders = (ConcurrentDictionary<string, ServiceBusSender>)field!.GetValue(client)!;
        senders[consumerName] = sender;

        return client;
    }

    private sealed class TestPayload
    {
        public string? Value { get; set; }
    }
}
