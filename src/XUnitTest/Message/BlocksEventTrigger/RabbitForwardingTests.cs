using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using XUnitTest.Delegation;

namespace XUnitTest.Message.BlocksEventTrigger;

[Collection("BlocksAuthStaticState")]
public sealed class RabbitForwardingTests : IDisposable
{
    private const string Queue = "blocks_iam_listener_user";
    private const string Exchange = "blocks_payment_lifecycle_topic";

    private sealed record Published(string Exchange, string RoutingKey, bool Mandatory, BasicProperties Properties, string Body);

    private readonly ConcurrentQueue<Published> _published = new();
    private readonly Mock<IChannel> _channel;

    public RabbitForwardingTests()
    {
        TriggerKit.UseTenant("T1");
        _channel = TriggerKit.Channel();
        _channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>((e, r, m, p, b, _) =>
                _published.Enqueue(new Published(e, r, m, p, Encoding.UTF8.GetString(b.Span))))
            .Returns(ValueTask.CompletedTask);
    }

    public void Dispose() => TriggerKit.ClearTenant();

    private RabbitMessageClient Client(
        BsonArray? catalog,
        out Mock<IKeyValueStore> store,
        RecordingLogger<BlocksEventTriggerForwarder>? forwarderLogger = null,
        RecordingLogger<RabbitMessageClient>? clientLogger = null,
        ActivitySource? source = null,
        IDelegationGrantFactory? grants = null)
    {
        store = TriggerKit.Store(catalog);
        var forwarder = TriggerKit.Forwarder(TriggerKit.Cache(store.Object), forwarderLogger);
        return TriggerKit.RabbitClient(forwarder, _channel.Object, clientLogger, source, grants);
    }

    private static ConsumerMessage<OrderPlaced> Order(string consumer = Queue) => new()
    {
        ConsumerName = consumer,
        Payload = new OrderPlaced { OrderId = "o-1", Secret = TriggerKit.PayloadMarker }
    };

    private void FailPublishesTo(string routingKey, Exception ex) =>
        _channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), routingKey, It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException(ex));

    [Fact]
    public async Task H2_H4_EnabledEventIsPublishedOnceToTheTriggerQueueAfterTheOriginal()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendToConsumerAsync(Order());

        var publishes = _published.ToList();
        Assert.Equal(2, publishes.Count);
        Assert.Equal(Queue, publishes[0].RoutingKey);
        Assert.Equal(string.Empty, publishes[1].Exchange);
        Assert.Equal(BlocksEventTriggerConstants.QueueName, publishes[1].RoutingKey);
        Assert.True(publishes[1].Mandatory);

        var originalEnvelope = JsonSerializer.Deserialize<Blocks.Genesis.Message>(publishes[0].Body)!;
        var trigger = TriggerKit.ReadForward(publishes[1].Body);
        Assert.Equal("OrderPlaced", trigger.EventName);
        Assert.Equal(TriggerKit.ServiceName, trigger.SourceService);
        Assert.Equal(Queue, trigger.ConsumerName);
        Assert.False(trigger.IsMassConsumer);
        Assert.Equal(originalEnvelope.Body, trigger.PayloadJson);
        Assert.Matches("^[0-9a-f]{32}$", trigger.EventId);
        Assert.Equal(DateTimeKind.Utc, trigger.Timestamp.Kind);
    }

    [Fact]
    public async Task H3_MassConsumerForwardIsFlaggedAndNamesTheExchange()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendToMassConsumerAsync(Order(Exchange) with { RoutingKey = "rk" });

        var publishes = _published.ToList();
        Assert.Equal(Exchange, publishes[0].Exchange);
        Assert.Equal("rk", publishes[0].RoutingKey);
        var trigger = TriggerKit.ReadForward(publishes[1].Body);
        Assert.True(trigger.IsMassConsumer);
        Assert.Equal(Exchange, trigger.ConsumerName);
        Assert.Equal(BlocksEventTriggerConstants.QueueName, publishes[1].RoutingKey);
    }

    [Fact]
    public async Task H3_PublicSendMessageToConsumerIsHookedToo()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendMessageToConsumerAsync(Order(Exchange), isExchange: true);

        Assert.True(TriggerKit.ReadForward(_published.Last().Body).IsMassConsumer);
    }

    [Fact]
    public async Task H5_ForwardCarriesTenantContextAndDelegationTtlAndIsNotScheduled()
    {
        var grants = DelegationTestDoubles.GrantFactory(DelegationTestDoubles.SampleGrantId());
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, grants: grants.Object);
        var ttl = TimeSpan.FromHours(5);

        await client.SendToConsumerAsync(Order() with
        {
            Context = "{\"provided\":\"context\"}",
            DelegationTtl = ttl,
            ScheduledEnqueueTimeUtc = DateTimeOffset.UtcNow.AddDays(1)
        });

        var publishes = _published.ToList();
        Assert.Equal(2, publishes.Count);
        var headers = publishes[1].Properties.Headers!;
        Assert.Equal("T1", headers["TenantId"]);
        Assert.Equal("{\"provided\":\"context\"}", headers["SecurityContext"]);
        Assert.Equal(DelegationTestDoubles.SampleGrantId(), headers[DelegationConstants.DelegationGrantHeader]);
        grants.Verify(g => g.CreateForSendAsync(ttl), Times.Exactly(2));
        // RabbitMQ has no scheduling: like the original, the forward was published immediately (count above).
    }

    [Fact]
    public async Task C1_NotEnabledIsNotForwarded()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", false)), out _);

        await client.SendToConsumerAsync(Order());
        await client.SendToConsumerAsync(new ConsumerMessage<SmsQueued> { ConsumerName = Queue, Payload = new SmsQueued() });

        Assert.Equal(2, _published.Count);
        Assert.DoesNotContain(_published, p => p.RoutingKey == BlocksEventTriggerConstants.QueueName);
    }

    [Fact]
    public async Task C2_RecursionGuardUsesTheWireTypeEvenWhenTIsObject()
    {
        var client = Client(TriggerKit.Catalog((nameof(BlocksEventTriggerMessage), true)), out var store);

        await client.SendToConsumerAsync(new ConsumerMessage<object>
        {
            ConsumerName = BlocksEventTriggerConstants.QueueName,
            Payload = new BlocksEventTriggerMessage { EventName = "X" }
        });
        await client.SendToConsumerAsync(new ConsumerMessage<BlocksEventTriggerMessage>
        {
            ConsumerName = BlocksEventTriggerConstants.QueueName,
            Payload = new BlocksEventTriggerMessage { EventName = "Y" }
        });

        Assert.Equal(2, _published.Count);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task C2_RuntimeTypeIsTheEventNameWhenTIsABaseType()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendToConsumerAsync(new ConsumerMessage<object> { ConsumerName = Queue, Payload = new OrderPlaced() });

        Assert.Equal("OrderPlaced", TriggerKit.ReadForward(_published.Last().Body).EventName);
    }

    [Fact]
    public async Task C3_NoTenantMeansNoCatalogReadAndNoForward()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out var store);
        TriggerKit.UseTenant(null);

        await client.SendToConsumerAsync(Order());

        Assert.Single(_published);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task C6_ForwardPublishFailureReachesTheForwarderAndLeavesTheOriginal()
    {
        FailPublishesTo(BlocksEventTriggerConstants.QueueName, new InvalidOperationException("channel closed " + TriggerKit.PayloadMarker));
        var forwarderLogger = new RecordingLogger<BlocksEventTriggerForwarder>();
        var clientLogger = new RecordingLogger<RabbitMessageClient>();
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, forwarderLogger, clientLogger);

        await client.SendToConsumerAsync(Order());

        Assert.Single(_published);
        _channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), BlocksEventTriggerConstants.QueueName, It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Once);

        var warning = Assert.Single(forwarderLogger.At(LogLevel.Warning));
        Assert.Contains("T1", warning.Message);
        Assert.Contains("OrderPlaced", warning.Message);
        Assert.Empty(clientLogger.At(LogLevel.Error));
        Assert.DoesNotContain(TriggerKit.PayloadMarker, forwarderLogger.AllText());
    }

    [Fact]
    public async Task C6_C14_ReturnedMessageIsLoggedWithoutItsBody()
    {
        var clientLogger = new RecordingLogger<RabbitMessageClient>();
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, clientLogger: clientLogger);
        await client.SendToConsumerAsync(Order());
        var forward = _published.Last();

        _channel.Raise(c => c.BasicReturnAsync += null, _channel.Object, new BasicReturnEventArgs(
            replyCode: 312,
            replyText: "NO_ROUTE",
            exchange: string.Empty,
            routingKey: BlocksEventTriggerConstants.QueueName,
            basicProperties: forward.Properties,
            body: Encoding.UTF8.GetBytes(forward.Body)));

        var warning = Assert.Single(clientLogger.At(LogLevel.Warning));
        Assert.Contains("Message returned", warning.Message);
        Assert.Contains("312", warning.Message);
        Assert.Contains(BlocksEventTriggerConstants.QueueName, warning.Message);
        Assert.Contains(nameof(BlocksEventTriggerMessage), warning.Message);
        Assert.Contains("T1", warning.Message);
        Assert.Contains(Encoding.UTF8.GetByteCount(forward.Body).ToString(), warning.Message);
        Assert.DoesNotContain(TriggerKit.PayloadMarker, clientLogger.AllText());
        Assert.DoesNotContain("o-1", clientLogger.AllText());
        Assert.DoesNotContain(warning.State, kv => kv.Key == "Body");
    }

    [Fact]
    public async Task C14_ReturnedOriginalIsAlsoLoggedWithoutItsBody_AndByteArrayTenantHeaderIsRead()
    {
        var clientLogger = new RecordingLogger<RabbitMessageClient>();
        var client = Client(TriggerKit.Catalog(), out _, clientLogger: clientLogger);
        await client.SendToConsumerAsync(Order());

        _channel.Raise(c => c.BasicReturnAsync += null, _channel.Object, new BasicReturnEventArgs(
            replyCode: 312,
            replyText: "NO_ROUTE",
            exchange: string.Empty,
            routingKey: Queue,
            basicProperties: new BasicProperties { Headers = new Dictionary<string, object?> { ["TenantId"] = Encoding.UTF8.GetBytes("T9") } },
            body: Encoding.UTF8.GetBytes(_published.Single().Body)));

        var warning = Assert.Single(clientLogger.At(LogLevel.Warning));
        Assert.Contains("Type=OrderPlaced", warning.Message);
        Assert.Contains("TenantId=T9", warning.Message);
        Assert.DoesNotContain(TriggerKit.PayloadMarker, clientLogger.AllText());
    }

    [Theory]
    [InlineData("payload", "unknown")]
    [InlineData("[1,2]", "unknown")]
    [InlineData("{\"Body\":\"{\\\"a\\\":{}}\",\"Extra\":{\"x\":[1]},\"Type\":\"OrderPlaced\"}", "OrderPlaced")]
    [InlineData("{\"Body\":\"x\"}", "unknown")]
    [InlineData("{\"Type\":5}", "unknown")]
    [InlineData("{\"Body\":", "unknown")]
    [InlineData("", "unknown")]
    public void ReadWireType_ParsesOnlyTheEnvelopeType(string body, string expected)
    {
        Assert.Equal(expected, RabbitMessageClient.ReadWireType(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task C7_OriginalFailureIsStillSwallowedAndNothingIsForwarded()
    {
        FailPublishesTo(Queue, new InvalidOperationException("broker down"));
        var clientLogger = new RecordingLogger<RabbitMessageClient>();
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out var store, clientLogger: clientLogger);

        await client.SendToConsumerAsync(Order());

        _channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), BlocksEventTriggerConstants.QueueName, It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(clientLogger.At(LogLevel.Error));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task C8_OversizeForwardIsDropped()
    {
        var forwarderLogger = new RecordingLogger<BlocksEventTriggerForwarder>();
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, forwarderLogger);

        await client.SendToConsumerAsync(new ConsumerMessage<OrderPlaced> { ConsumerName = Queue, Payload = new OrderPlaced { OrderId = new string('a', 200_000) } });

        Assert.Single(_published);
        Assert.Single(forwarderLogger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task C9_CrossTenantNegative()
    {
        var store = TriggerKit.Store(tenant => tenant == "T1" ? TriggerKit.Catalog(("OrderPlaced", true)) : TriggerKit.Catalog(("OrderPlaced", false)));
        var client = TriggerKit.RabbitClient(TriggerKit.Forwarder(TriggerKit.Cache(store.Object)), _channel.Object);

        TriggerKit.UseTenant("T2");
        await client.SendToConsumerAsync(Order());
        TriggerKit.UseTenant("T1");
        await client.SendToConsumerAsync(Order());

        var forwards = _published.Where(p => p.RoutingKey == BlocksEventTriggerConstants.QueueName).ToList();
        Assert.Equal("T1", Assert.Single(forwards).Properties.Headers!["TenantId"]);
    }

    [Fact]
    public async Task ZeroIo_OnCacheHits_WithStrictMocksAfterManySends()
    {
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", false)), MockBehavior.Strict);
        var redis = TriggerKit.CacheClient(MockBehavior.Strict);
        var client = TriggerKit.RabbitClient(TriggerKit.Forwarder(TriggerKit.Cache(store.Object, redis.Object)), TriggerKit.Channel().Object);

        for (var i = 0; i < 10_000; i++)
        {
            await client.SendToConsumerAsync(Order());
        }

        store.Verify(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
        redis.Verify(c => c.SubscribeAsync(BlocksEventTriggerConstants.InvalidationChannel, It.IsAny<Action<StackExchange.Redis.RedisChannel, StackExchange.Redis.RedisValue>>()), Times.Once);
        redis.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoOriginalSendFailure_WhenTheCacheItselfThrows()
    {
        var clientLogger = new RecordingLogger<RabbitMessageClient>();
        var client = TriggerKit.RabbitClient(TriggerKit.Forwarder(new ThrowingCatalogCache { Synchronous = true }), _channel.Object, clientLogger);

        await client.SendToConsumerAsync(Order());

        Assert.Single(_published);
        Assert.Empty(clientLogger.At(LogLevel.Error));
    }

    [Fact]
    public async Task H11_SpanTagsOnTheProducerSpan()
    {
        var sourceName = "bet-rabbit-" + Guid.NewGuid().ToString("N");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = TriggerKit.Listen(sourceName, stopped);
        using var source = new ActivitySource(sourceName);
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, source: source);

        await client.SendToConsumerAsync(Order());
        await client.SendToConsumerAsync(new ConsumerMessage<SmsQueued> { ConsumerName = Queue, Payload = new SmsQueued { Text = TriggerKit.PayloadMarker } });

        Assert.Contains(stopped, a => a.GetTagItem("blocks.event_trigger.forwarded") is true);
        Assert.Contains(stopped, a => "not_enabled".Equals(a.GetTagItem("blocks.event_trigger.skip_reason")));
        var forwardSpan = stopped.Single(a => a.GetTagItem("messaging.destination.name") as string == BlocksEventTriggerConstants.QueueName);
        Assert.Equal("queue", forwardSpan.GetTagItem("messaging.destination.kind"));
        var allTags = string.Join("|", stopped.SelectMany(a => a.TagObjects).Select(t => $"{t.Key}={t.Value}"));
        Assert.DoesNotContain(TriggerKit.PayloadMarker, allTags);
    }

    [Fact]
    public async Task H5_ForwardCarriesTheOriginalsBaggage()
    {
        // Only the caller's span is sampled, so Activity.Current at publish time is the span carrying the baggage.
        var parentSourceName = "bet-rabbit-baggage-" + Guid.NewGuid().ToString("N");
        using var listener = TriggerKit.Listen(parentSourceName, new ConcurrentQueue<Activity>());
        using var parentSource = new ActivitySource(parentSourceName);
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        using (var parent = parentSource.StartActivity("parent"))
        {
            parent!.AddBaggage("flow", "checkout");
            await client.SendToConsumerAsync(Order());
        }

        var publishes = _published.ToList();
        Assert.Equal(2, publishes.Count);
        Assert.All(publishes, p => Assert.Contains("checkout", (string)p.Properties.Headers!["Baggage"]!));
    }

    [Fact]
    public async Task C13_OriginalPublishIsIdenticalWithTheHookOnOrOff()
    {
        var offChannel = TriggerKit.Channel();
        var offPublished = new List<Published>();
        offChannel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>((e, r, m, p, b, _) =>
                offPublished.Add(new Published(e, r, m, p, Encoding.UTF8.GetString(b.Span))))
            .Returns(ValueTask.CompletedTask);
        var off = TriggerKit.RabbitClient(null, offChannel.Object);
        var on = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);
        var message = Order() with { RoutingKey = "rk", DelegationTtl = TimeSpan.FromHours(1) };

        await off.SendToMassConsumerAsync(message with { ConsumerName = Exchange });
        await on.SendToMassConsumerAsync(message with { ConsumerName = Exchange });

        var before = Assert.Single(offPublished);
        var after = _published.First();
        Assert.Equal(before.Body, after.Body);
        Assert.Equal(before.Exchange, after.Exchange);
        Assert.Equal(before.RoutingKey, after.RoutingKey);
        Assert.Equal(before.Mandatory, after.Mandatory);
        Assert.Equal(before.Properties.DeliveryMode, after.Properties.DeliveryMode);
        Assert.Equal(before.Properties.Expiration, after.Properties.Expiration);
        Assert.Equal(before.Properties.Headers!.Keys.OrderBy(k => k), after.Properties.Headers!.Keys.OrderBy(k => k));
        foreach (var key in before.Properties.Headers.Keys)
        {
            Assert.Equal(before.Properties.Headers[key], after.Properties.Headers[key]);
        }

        Assert.Equal(2, _published.Count);
    }
}
