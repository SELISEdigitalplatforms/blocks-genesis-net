using Azure.Messaging.ServiceBus;
using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace XUnitTest.Delegation;

/// <summary>
/// End-to-end wiring on both transports: the send stamps the header, the worker reads it into
/// <see cref="DelegatedTokenContext"/>, and the grant is released only after a successful settle.
/// </summary>
public class DelegationMessageFlowTests : IDisposable
{
    private const string AzureConnection =
        "Endpoint=sb://unit-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=01234567890123456789012345678901234567890123456789=";

    private readonly bool _originalTestMode = BlocksContext.IsTestMode;

    public void Dispose()
    {
        BlocksContext.SetContext(null);
        DelegatedTokenContext.Clear();
        BlocksContext.IsTestMode = _originalTestMode;
    }

    private sealed record Payload
    {
        public string Value { get; init; } = string.Empty;
    }

    // ---------------------------------------------------------------- Azure send

    [Fact]
    public async Task AzureSend_ShouldStampTheDelegationGrantHeader_WhenAGrantIsCreated()
    {
        var grantId = DelegationTestDoubles.SampleGrantId();
        var factory = DelegationTestDoubles.GrantFactory(grantId);

        var captured = await CaptureAzureSend(factory.Object, new ConsumerMessage<Payload>
        {
            ConsumerName = "orders-queue",
            Payload = new Payload { Value = "v" }
        });

        Assert.True(captured.ApplicationProperties.TryGetValue(DelegationConstants.DelegationGrantHeader, out var header));
        Assert.Equal(grantId, header);

        // SecurityContext is still sent: it remains the context and tracing channel.
        Assert.True(captured.ApplicationProperties.ContainsKey("SecurityContext"));
    }

    [Fact]
    public async Task AzureSend_ShouldOmitTheHeader_WhenThereIsNoGrant()
    {
        var captured = await CaptureAzureSend(DelegationTestDoubles.NoGrantFactory(), new ConsumerMessage<Payload>
        {
            ConsumerName = "orders-queue",
            Payload = new Payload { Value = "v" }
        });

        Assert.False(captured.ApplicationProperties.ContainsKey(DelegationConstants.DelegationGrantHeader));
    }

    [Fact]
    public async Task AzureSend_ShouldPassTheTtlOverrideToTheFactory()
    {
        var factory = DelegationTestDoubles.GrantFactory(DelegationTestDoubles.SampleGrantId());

        await CaptureAzureSend(factory.Object, new ConsumerMessage<Payload>
        {
            ConsumerName = "orders-queue",
            Payload = new Payload { Value = "v" },
            DelegationTtl = TimeSpan.FromHours(5)
        });

        factory.Verify(f => f.CreateForSendAsync(TimeSpan.FromHours(5)), Times.Once);
    }

    private static async Task<ServiceBusMessage> CaptureAzureSend(IDelegationGrantFactory grantFactory, ConsumerMessage<Payload> message)
    {
        var sender = new Mock<ServiceBusSender>();
        ServiceBusMessage? captured = null;

        sender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback((ServiceBusMessage sent, CancellationToken _) => captured = sent)
            .Returns(Task.CompletedTask);

        var client = new AzureMessageClient(
            new MessageConfiguration
            {
                Connection = AzureConnection,
                AzureServiceBusConfiguration = new AzureServiceBusConfiguration { Queues = [], Topics = [] }
            },
            new ActivitySource("delegation-azure-send-" + Guid.NewGuid().ToString("N")),
            grantFactory);

        var senders = (ConcurrentDictionary<string, ServiceBusSender>)typeof(AzureMessageClient)
            .GetField("_senders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
        senders[message.ConsumerName] = sender.Object;

        await client.SendToConsumerAsync(message);

        Assert.NotNull(captured);
        return captured!;
    }

    // ---------------------------------------------------------------- Azure worker

    [Fact]
    public async Task AzureWorker_ShouldReleaseTheGrant_AfterASuccessfulSettle()
    {
        var grantId = DelegationTestDoubles.SampleGrantId();
        var store = new Mock<IDelegationGrantStore>();
        var provider = new Mock<IDelegatedTokenProvider>();

        var argsMock = await RunAzureWorker(grantId, store.Object, provider.Object, envelopeBody: "{\"Type\":\"NoRoute\",\"Body\":\"{}\"}");

        argsMock.Verify(a => a.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.DeleteAsync(grantId), Times.Once);
        provider.Verify(p => p.Invalidate(grantId), Times.Once);
    }

    [Fact]
    public async Task AzureWorker_ShouldRetainTheGrant_WhenProcessingFails()
    {
        var grantId = DelegationTestDoubles.SampleGrantId();
        var store = new Mock<IDelegationGrantStore>();
        var provider = new Mock<IDelegatedTokenProvider>();

        // A malformed envelope makes the dispatch throw, so the message is dead-lettered rather
        // than completed. The grant must survive so a redelivery can still mint a token.
        var argsMock = await RunAzureWorker(grantId, store.Object, provider.Object, envelopeBody: "not-json-at-all");

        argsMock.Verify(a => a.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.DeleteAsync(It.IsAny<string>()), Times.Never);
        provider.Verify(p => p.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AzureWorker_ShouldNotCallTheStore_WhenTheMessageHasNoGrant()
    {
        var store = new Mock<IDelegationGrantStore>();
        var provider = new Mock<IDelegatedTokenProvider>();

        await RunAzureWorker(null, store.Object, provider.Object, envelopeBody: "{\"Type\":\"NoRoute\",\"Body\":\"{}\"}");

        store.Verify(s => s.DeleteAsync(It.IsAny<string>()), Times.Never);
        provider.Verify(p => p.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AzureWorker_ShouldExposeTheGrantToTheHandler()
    {
        var grantId = DelegationTestDoubles.SampleGrantId();
        var observed = new List<string?>();

        await RunAzureWorker(
            grantId,
            new Mock<IDelegationGrantStore>().Object,
            new Mock<IDelegatedTokenProvider>().Object,
            envelopeBody: "{\"Type\":\"" + nameof(Payload) + "\",\"Body\":\"{}\"}",
            observeGrant: observed);

        Assert.Equal(grantId, Assert.Single(observed));
    }

    private static async Task<Mock<Azure.Messaging.ServiceBus.ProcessMessageEventArgs>> RunAzureWorker(
        string? grantId,
        IDelegationGrantStore store,
        IDelegatedTokenProvider provider,
        string envelopeBody,
        List<string?>? observeGrant = null)
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();

            var properties = new Dictionary<string, object>
            {
                ["TraceId"] = "0123456789abcdef0123456789abcdef",
                ["SpanId"] = "0123456789abcdef",
                ["TenantId"] = "tenant-1",
                ["SecurityContext"] = "{}",
                ["Baggage"] = "{}"
            };

            if (grantId is not null)
            {
                properties[DelegationConstants.DelegationGrantHeader] = grantId;
            }

            var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: BinaryData.FromString(envelopeBody),
                messageId: "delegation-" + Guid.NewGuid().ToString("N"),
                properties: properties);

            var receiver = new Mock<ServiceBusReceiver>();
            var argsMock = new Mock<Azure.Messaging.ServiceBus.ProcessMessageEventArgs>(message, receiver.Object, CancellationToken.None);

            var worker = new AzureMessageWorker(
                new Mock<ILogger<AzureMessageWorker>>().Object,
                new MessageConfiguration
                {
                    Connection = AzureConnection,
                    AzureServiceBusConfiguration = new AzureServiceBusConfiguration { Queues = [], Topics = [] }
                },
                CreateConsumer(observeGrant),
                new ActivitySource("delegation-azure-worker-" + Guid.NewGuid().ToString("N")),
                store,
                provider);

            var handler = typeof(AzureMessageWorker).GetMethod("MessageHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)handler.Invoke(worker, [argsMock.Object])!;

            return argsMock;
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    // ---------------------------------------------------------------- Rabbit send

    [Fact]
    public async Task RabbitSend_ShouldStampTheDelegationGrantHeader_WhenAGrantIsCreated()
    {
        var grantId = DelegationTestDoubles.SampleGrantId();
        var headers = await CaptureRabbitSend(DelegationTestDoubles.GrantFactory(grantId).Object);

        Assert.True(headers.TryGetValue(DelegationConstants.DelegationGrantHeader, out var header));
        Assert.Equal(grantId, header);
        Assert.True(headers.ContainsKey("SecurityContext"));
    }

    [Fact]
    public async Task RabbitSend_ShouldOmitTheHeader_WhenThereIsNoGrant()
    {
        var headers = await CaptureRabbitSend(DelegationTestDoubles.NoGrantFactory());

        Assert.False(headers.ContainsKey(DelegationConstants.DelegationGrantHeader));
    }

    private static async Task<IDictionary<string, object?>> CaptureRabbitSend(IDelegationGrantFactory grantFactory)
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        IDictionary<string, object?>? captured = null;
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string __, bool ___, BasicProperties properties, ReadOnlyMemory<byte> ____, CancellationToken _____) =>
                captured = properties.Headers)
            .Returns(ValueTask.CompletedTask);

        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.Setup(s => s.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(s => s.RabbitMqChannel).Returns(channel.Object);

        var client = new RabbitMessageClient(
            new Mock<ILogger<RabbitMessageClient>>().Object,
            rabbitService.Object,
            new MessageConfiguration { RabbitMqConfiguration = new RabbitMqConfiguration() },
            new ActivitySource("delegation-rabbit-send-" + Guid.NewGuid().ToString("N")),
            grantFactory);

        await client.SendToConsumerAsync(new ConsumerMessage<Payload>
        {
            ConsumerName = "orders.queue",
            Payload = new Payload { Value = "v" }
        });

        Assert.NotNull(captured);
        return captured!;
    }

    // ---------------------------------------------------------------- Rabbit worker

    [Fact]
    public async Task RabbitWorker_ShouldReleaseTheGrant_AfterASuccessfulAck()
    {
        var grantId = DelegationTestDoubles.SampleGrantId();
        var store = new Mock<IDelegationGrantStore>();
        var provider = new Mock<IDelegatedTokenProvider>();

        var channel = await RunRabbitWorker(grantId, store.Object, provider.Object, body: "{\"Type\":\"NoRoute\",\"Body\":\"{}\"}");

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.DeleteAsync(grantId), Times.Once);
        provider.Verify(p => p.Invalidate(grantId), Times.Once);
    }

    [Fact]
    public async Task RabbitWorker_ShouldExposeTheGrantToTheHandler()
    {
        var grantId = DelegationTestDoubles.SampleGrantId();
        var observed = new List<string?>();

        await RunRabbitWorker(
            grantId,
            new Mock<IDelegationGrantStore>().Object,
            new Mock<IDelegatedTokenProvider>().Object,
            body: "{\"Type\":\"" + nameof(Payload) + "\",\"Body\":\"{}\"}",
            observeGrant: observed);

        Assert.Equal(grantId, Assert.Single(observed));
    }

    [Fact]
    public async Task RabbitWorker_ShouldNotCallTheStore_WhenTheMessageHasNoGrant()
    {
        var store = new Mock<IDelegationGrantStore>();
        var provider = new Mock<IDelegatedTokenProvider>();

        await RunRabbitWorker(null, store.Object, provider.Object, body: "{\"Type\":\"NoRoute\",\"Body\":\"{}\"}");

        store.Verify(s => s.DeleteAsync(It.IsAny<string>()), Times.Never);
        provider.Verify(p => p.Invalidate(It.IsAny<string>()), Times.Never);
    }

    private static async Task<Mock<IChannel>> RunRabbitWorker(
        string? grantId,
        IDelegationGrantStore store,
        IDelegatedTokenProvider provider,
        string body,
        List<string?>? observeGrant = null)
    {
        const string queueName = "orders.queue";

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        channel
            .Setup(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var worker = new RabbitMessageWorker(
            new Mock<ILogger<RabbitMessageWorker>>().Object,
            new MessageConfiguration
            {
                RabbitMqConfiguration = new RabbitMqConfiguration
                {
                    ConsumerSubscriptions = [ConsumerSubscription.BindToQueue(queueName, 3)]
                }
            },
            new Mock<IRabbitMqService>().Object,
            CreateConsumer(observeGrant),
            new ActivitySource("delegation-rabbit-worker-" + Guid.NewGuid().ToString("N")),
            store,
            provider);

        typeof(RabbitMessageWorker)
            .GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(worker, channel.Object);

        var eventArgs = CreateDeliverArgs(queueName, body, grantId);

        var handler = typeof(RabbitMessageWorker).GetMethod("ProcessMessageInternalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)handler.Invoke(worker, [eventArgs])!;

        return channel;
    }

    private static BasicDeliverEventArgs CreateDeliverArgs(string routingKey, string body, string? grantId)
    {
        var eventArgs = (BasicDeliverEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(BasicDeliverEventArgs));

        var headers = new Dictionary<string, object?>
        {
            ["TenantId"] = Encoding.UTF8.GetBytes("tenant-1"),
            ["TraceId"] = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"),
            ["SpanId"] = Encoding.UTF8.GetBytes("0123456789abcdef"),
            ["SecurityContext"] = Encoding.UTF8.GetBytes("{}"),
            ["Baggage"] = Encoding.UTF8.GetBytes("{}")
        };

        if (grantId is not null)
        {
            headers[DelegationConstants.DelegationGrantHeader] = Encoding.UTF8.GetBytes(grantId);
        }

        SetMember(eventArgs, "RoutingKey", routingKey);
        SetMember(eventArgs, "DeliveryTag", 1UL);
        SetMember(eventArgs, "BasicProperties", new BasicProperties { Headers = headers });
        SetMember(eventArgs, "Body", new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)));

        return eventArgs;
    }

    private static void SetMember(object target, string name, object value)
    {
        var type = target.GetType();

        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.SetMethod is not null)
        {
            property.SetValue(target, value);
            return;
        }

        var field = type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    // ---------------------------------------------------------------- consumer probe

    /// <summary>
    /// A consumer that records what <see cref="DelegatedTokenContext"/> holds while the handler runs.
    /// This is the whole point of the header: handler code sees a grant without doing anything.
    /// </summary>
    private sealed class GrantObservingConsumer : IConsumer<Payload>
    {
        private readonly List<string?> _observed;

        public GrantObservingConsumer(List<string?> observed) => _observed = observed;

        public Task Consume(Payload message)
        {
            lock (_observed) _observed.Add(DelegatedTokenContext.Current);
            return Task.CompletedTask;
        }
    }

    private static Consumer CreateConsumer(List<string?>? observeGrant)
    {
        var services = new ServiceCollection();

        if (observeGrant is not null)
        {
            // Registered by implementation type: RoutingTable skips factory registrations, so a
            // closure-based probe would never be routed to.
            services.AddSingleton(observeGrant);
            services.AddSingleton<IConsumer<Payload>, GrantObservingConsumer>();
        }

        var routing = new RoutingTable(services);
        return new Consumer(new Mock<ILogger<Consumer>>().Object, services.BuildServiceProvider(), routing);
    }
}
