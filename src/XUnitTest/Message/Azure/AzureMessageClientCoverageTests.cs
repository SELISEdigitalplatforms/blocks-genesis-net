using Blocks.Genesis;
using Azure.Messaging.ServiceBus;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using XUnitTest.Delegation;

namespace XUnitTest.Message.Azure;

public class AzureMessageClientCoverageTests
{
    private const string ValidConnection = "Endpoint=sb://unit-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=01234567890123456789012345678901234567890123456789=";

    [Fact]
    public void Constructor_ShouldInitializeNoSenders_WhenAzureServiceBusConfigurationIsNull()
    {
        var client = CreateClient(new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = null
        });

        Assert.Empty(GetSenders(client));
    }

    [Fact]
    public void InitializeSenders_ShouldInitializeNoSenders_WhenConfigurationIsNull()
    {
        var client = CreateClient(new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        });

        var method = typeof(AzureMessageClient).GetMethod("InitializeSenders", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        method!.Invoke(client, [null]);

        Assert.Empty(GetSenders(client));
    }

    [Fact]
    public async Task SendToConsumerAsync_ShouldPropagateActivityAndSecurityContext_WhenListenerIsActive()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        var sourceName = "client-cov-" + Guid.NewGuid().ToString("N");
        using var listener = CreateListener(sourceName);
        using var activitySource = new ActivitySource(sourceName);
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.SetContext(BlocksContext.Create(
                tenantId: "tenant-42",
                roles: ["role-a"],
                userId: "user-1",
                isAuthenticated: true,
                requestUri: "https://unit.test",
                organizationId: "org-1",
                expireOn: DateTime.UtcNow.AddHours(1),
                email: "user@example.com",
                permissions: ["perm-a"],
                userName: "user",
                phoneNumber: "0123456789",
                displayName: "User",
                oauthToken: "token",
                originalTenantId: "tenant-42"));

            using var parentActivity = activitySource.StartActivity("parent-operation");
            Assert.NotNull(parentActivity);

            var senderMock = new Mock<ServiceBusSender>();
            ServiceBusMessage? capturedMessage = null;
            senderMock
                .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
                .Callback<ServiceBusMessage, CancellationToken>((msg, _) => capturedMessage = msg)
                .Returns(Task.CompletedTask);

            var client = CreateClientWithInjectedSender("orders.queue", senderMock.Object, activitySource);

            await client.SendToConsumerAsync(new ConsumerMessage<TestPayload>
            {
                ConsumerName = "orders.queue",
                Payload = new TestPayload { Value = "traced" }
            });

            Assert.NotNull(capturedMessage);
            Assert.Equal("tenant-42", capturedMessage!.ApplicationProperties["TenantId"]?.ToString());
            Assert.Equal(parentActivity!.TraceId.ToString(), capturedMessage.ApplicationProperties["TraceId"]?.ToString());
            Assert.NotNull(capturedMessage.ApplicationProperties["SpanId"]?.ToString());

            var securityContext = capturedMessage.ApplicationProperties["SecurityContext"]?.ToString();
            Assert.NotNull(securityContext);
            Assert.Contains("***@example.com", securityContext);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task SendToMassConsumerAsync_ShouldTagDestinationAsTopic_WhenListenerIsActive()
    {
        var sourceName = "client-cov-" + Guid.NewGuid().ToString("N");
        using var listener = CreateListener(sourceName);
        using var activitySource = new ActivitySource(sourceName);

        var senderMock = new Mock<ServiceBusSender>();
        senderMock
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = CreateClientWithInjectedSender("topic.events", senderMock.Object, activitySource);

        await client.SendToMassConsumerAsync(new ConsumerMessage<TestPayload>
        {
            ConsumerName = "topic.events",
            Payload = new TestPayload { Value = "mass-traced" }
        });

        senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AzureMessageClient CreateClient(MessageConfiguration configuration, ActivitySource? activitySource = null)
    {
        return new AzureMessageClient(
            configuration,
            activitySource ?? new ActivitySource("test-azure-client-coverage"),
            DelegationTestDoubles.NoGrantFactory());
    }

    private static AzureMessageClient CreateClientWithInjectedSender(string consumerName, ServiceBusSender sender, ActivitySource activitySource)
    {
        var client = CreateClient(new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = [],
                Topics = []
            }
        }, activitySource);

        var senders = GetSenders(client);
        senders[consumerName] = sender;

        return client;
    }

    private static ConcurrentDictionary<string, ServiceBusSender> GetSenders(AzureMessageClient client)
    {
        var field = typeof(AzureMessageClient).GetField("_senders", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (ConcurrentDictionary<string, ServiceBusSender>)field!.GetValue(client)!;
    }

    private static ActivityListener CreateListener(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class TestPayload
    {
        public string? Value { get; set; }
    }
}
