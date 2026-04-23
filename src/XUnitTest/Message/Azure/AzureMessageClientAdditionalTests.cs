using Blocks.Genesis;
using Azure.Messaging.ServiceBus;
using Moq;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace XUnitTest.Message.Azure;

public class AzureMessageClientAdditionalTests
{
    private const string ValidConnection =
        "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=01234567890123456789012345678901234567890123456789=";

    [Fact]
    public void Constructor_ShouldInitializeWithEmptySenders_WhenAzureServiceBusConfigurationIsNull()
    {
        var configuration = new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = null
        };

        var client = new AzureMessageClient(configuration, new ActivitySource("test-azure-client-null-cfg"));

        var field = typeof(AzureMessageClient).GetField("_senders", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var senders = (ConcurrentDictionary<string, ServiceBusSender>)field!.GetValue(client)!;
        Assert.Empty(senders);
    }

    [Fact]
    public void BuildTransportContextJson_ShouldReturnProvidedContext_WhenProvided()
    {
        var method = typeof(AzureMessageClient).GetMethod(
            "BuildTransportContextJson",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, [null, "{\"ctx\":\"yes\"}"])!;

        Assert.Equal("{\"ctx\":\"yes\"}", result);
    }

    [Fact]
    public void BuildTransportContextJson_ShouldSerializeSanitizedContext_WhenProvidedIsEmpty()
    {
        var method = typeof(AzureMessageClient).GetMethod(
            "BuildTransportContextJson",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, [null, string.Empty])!;

        Assert.False(string.IsNullOrWhiteSpace(result));
        // Must be valid JSON
        using var _ = JsonDocument.Parse(result);
    }

    [Fact]
    public async Task SendToMassConsumerAsync_ShouldSetTopicDestinationKind_OnActivity()
    {
        // Force Activity creation by attaching a listener.
        var activitySource = new ActivitySource("test-azure-client-activity");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-azure-client-activity",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { }
        };
        ActivitySource.AddActivityListener(listener);

        var senderMock = new Mock<ServiceBusSender>();
        senderMock
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var configuration = new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration { Queues = [], Topics = [] }
        };
        var client = new AzureMessageClient(configuration, activitySource);
        var field = typeof(AzureMessageClient).GetField("_senders", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var senders = (ConcurrentDictionary<string, ServiceBusSender>)field.GetValue(client)!;
        senders["topic.events"] = senderMock.Object;

        await client.SendToMassConsumerAsync(new ConsumerMessage<Payload>
        {
            ConsumerName = "topic.events",
            Payload = new Payload { Value = "v" },
            Context = string.Empty
        });

        senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ConsumerMessage_ObsoleteScheduledProperty_ShouldProxyToCanonicalProperty()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(10);

#pragma warning disable CS0618 // Obsolete member intentionally exercised for coverage.
        var msg = new ConsumerMessage<Payload>
        {
            ConsumerName = "q",
            Payload = new Payload(),
            SccheduledEnqueueTimeUtc = at
        };

        Assert.Equal(at, msg.ScheduledEnqueueTimeUtc);
        Assert.Equal(at, msg.SccheduledEnqueueTimeUtc);
#pragma warning restore CS0618
    }

    private sealed class Payload
    {
        public string? Value { get; set; }
    }
}
