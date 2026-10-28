using Azure.Messaging.ServiceBus;
using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Moq;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using XUnitTest.Delegation;

namespace XUnitTest.Message.BlocksEventTrigger;

[Collection("BlocksAuthStaticState")]
public sealed class AzureForwardingTests : IDisposable
{
    private const string Queue = "blocks_iam_listener_user";
    private const string Topic = "blocks_payment_lifecycle_topic";

    private readonly RecordingSender _original = new(Queue);
    private readonly RecordingSender _topic = new(Topic);
    private readonly RecordingSender _trigger = new(BlocksEventTriggerConstants.QueueName);

    public AzureForwardingTests() => TriggerKit.UseTenant("T1");

    public void Dispose() => TriggerKit.ClearTenant();

    private Dictionary<string, ServiceBusSender> Senders() => new()
    {
        [Queue] = _original,
        [Topic] = _topic,
        [BlocksEventTriggerConstants.QueueName] = _trigger
    };

    private AzureMessageClient Client(
        BsonArray? catalog,
        out Mock<IKeyValueStore> store,
        RecordingLogger<BlocksEventTriggerForwarder>? logger = null,
        ActivitySource? source = null,
        IDelegationGrantFactory? grants = null)
    {
        store = TriggerKit.Store(catalog);
        var forwarder = TriggerKit.Forwarder(TriggerKit.Cache(store.Object), logger);
        return TriggerKit.AzureClient(forwarder, Senders(), source, grants);
    }

    private static ConsumerMessage<OrderPlaced> Order(string consumer = Queue) => new()
    {
        ConsumerName = consumer,
        Payload = new OrderPlaced { OrderId = "o-1", Secret = TriggerKit.PayloadMarker }
    };

    [Fact]
    public async Task H1_H4_EnabledEventForwardsOnceAfterTheOriginal()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendToConsumerAsync(Order());

        var original = Assert.Single(_original.Sent);
        var forward = Assert.Single(_trigger.Sent);
        var originalEnvelope = JsonSerializer.Deserialize<Blocks.Genesis.Message>(original.Body.ToString())!;
        var trigger = TriggerKit.ReadForward(forward.Body.ToString());

        Assert.Equal("OrderPlaced", trigger.EventName);
        Assert.Equal(TriggerKit.ServiceName, trigger.SourceService);
        Assert.Equal(Queue, trigger.ConsumerName);
        Assert.False(trigger.IsMassConsumer);
        Assert.Equal(originalEnvelope.Body, trigger.PayloadJson);
        Assert.Matches("^[0-9a-f]{32}$", trigger.EventId);
        Assert.Equal(DateTimeKind.Utc, trigger.Timestamp.Kind);
        Assert.Equal(new TriggerClock().Now.UtcDateTime, trigger.Timestamp);
    }

    [Fact]
    public async Task H4_EachForwardGetsANewEventId()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendToConsumerAsync(Order());
        await client.SendToConsumerAsync(Order());

        var ids = _trigger.Sent.Select(m => TriggerKit.ReadForward(m.Body.ToString()).EventId).ToList();
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task H4_SourceServiceIsEmptyWhenServiceNameIsNotSet()
    {
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true)));
        var forwarder = TriggerKit.Forwarder(TriggerKit.Cache(store.Object), serviceName: null);
        var client = TriggerKit.AzureClient(forwarder, Senders());

        await client.SendToConsumerAsync(Order());

        Assert.Equal(string.Empty, TriggerKit.ReadForward(Assert.Single(_trigger.Sent).Body.ToString()).SourceService);
    }

    [Fact]
    public async Task H3_MassConsumerForwardIsFlaggedAndNamesTheTopic()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendToMassConsumerAsync(Order(Topic));

        Assert.Single(_topic.Sent);
        var trigger = TriggerKit.ReadForward(Assert.Single(_trigger.Sent).Body.ToString());
        Assert.True(trigger.IsMassConsumer);
        Assert.Equal(Topic, trigger.ConsumerName);
    }

    [Fact]
    public async Task H5_ForwardCarriesTenantContextScheduleAndDelegationTtl()
    {
        var grants = DelegationTestDoubles.GrantFactory(DelegationTestDoubles.SampleGrantId());
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, grants: grants.Object);
        var at = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var ttl = TimeSpan.FromHours(3);

        await client.SendToConsumerAsync(Order() with
        {
            Context = "{\"provided\":\"context\"}",
            ScheduledEnqueueTimeUtc = at,
            DelegationTtl = ttl
        });

        var (original, originalAt) = Assert.Single(_original.Scheduled);
        var (forward, forwardAt) = Assert.Single(_trigger.Scheduled);
        Assert.Empty(_trigger.Sent);
        Assert.Equal(at, originalAt);
        Assert.Equal(at, forwardAt);
        Assert.Equal("T1", forward.ApplicationProperties["TenantId"]);
        Assert.Equal("{\"provided\":\"context\"}", forward.ApplicationProperties["SecurityContext"]);
        Assert.Equal(original.ApplicationProperties["SecurityContext"], forward.ApplicationProperties["SecurityContext"]);
        Assert.Equal(DelegationTestDoubles.SampleGrantId(), forward.ApplicationProperties[DelegationConstants.DelegationGrantHeader]);
        grants.Verify(g => g.CreateForSendAsync(ttl), Times.Exactly(2));
    }

    [Fact]
    public async Task H5_DerivedSecurityContextMatchesTheOriginal()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendToConsumerAsync(Order());

        var original = Assert.Single(_original.Sent);
        var forward = Assert.Single(_trigger.Sent);
        Assert.Equal(original.ApplicationProperties["SecurityContext"], forward.ApplicationProperties["SecurityContext"]);
        Assert.DoesNotContain("oauth-token-secret", forward.ApplicationProperties["SecurityContext"]!.ToString());
    }

    [Fact]
    public async Task C1_DisabledOrUncataloguedEventIsNotForwarded()
    {
        var client = Client(TriggerKit.Catalog(("SmsQueued", false)), out _);

        await client.SendToConsumerAsync(new ConsumerMessage<SmsQueued> { ConsumerName = Queue, Payload = new SmsQueued() });
        await client.SendToConsumerAsync(Order());

        Assert.Equal(2, _original.Sent.Count);
        Assert.Empty(_trigger.Sent);
    }

    [Fact]
    public async Task C2_TriggerMessageIsNeverForwardedEvenWhenCatalogued()
    {
        var client = Client(TriggerKit.Catalog((nameof(BlocksEventTriggerMessage), true)), out var store);

        await client.SendToConsumerAsync(new ConsumerMessage<BlocksEventTriggerMessage>
        {
            ConsumerName = BlocksEventTriggerConstants.QueueName,
            Payload = new BlocksEventTriggerMessage { EventName = "X" }
        });

        Assert.Single(_trigger.Sent);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task C3_NoTenantMeansNoCatalogReadAndNoForward()
    {
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true)), MockBehavior.Strict);
        var redis = TriggerKit.CacheClient(MockBehavior.Strict);
        var forwarder = TriggerKit.Forwarder(TriggerKit.Cache(store.Object, redis.Object));
        var client = TriggerKit.AzureClient(forwarder, Senders());

        TriggerKit.UseTenant(null);
        await client.SendToConsumerAsync(Order());
        TriggerKit.UseTenant("");
        await client.SendToConsumerAsync(Order());

        Assert.Equal(2, _original.Sent.Count);
        Assert.Empty(_trigger.Sent);
        store.VerifyNoOtherCalls();
        redis.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task C6_ForwardFailureLeavesTheOriginalAndLogsTenantAndEventOnly()
    {
        _trigger.Throw = new ServiceBusException("entity not found " + TriggerKit.PayloadMarker, ServiceBusFailureReason.MessagingEntityNotFound);
        var logger = new RecordingLogger<BlocksEventTriggerForwarder>();
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, logger);

        await client.SendToConsumerAsync(Order());

        Assert.Single(_original.Sent);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("T1", warning.Message);
        Assert.Contains("OrderPlaced", warning.Message);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(TriggerKit.PayloadMarker, logger.AllText());
    }

    [Fact]
    public async Task C6_ForwardIsNotRetried()
    {
        var calls = 0;
        var trigger = new Mock<ServiceBusSender>();
        trigger
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() => { calls++; return Task.FromException(new TimeoutException()); });
        var senders = Senders();
        senders[BlocksEventTriggerConstants.QueueName] = trigger.Object;
        var forwarder = TriggerKit.Forwarder(TriggerKit.Cache(TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true))).Object));
        var client = TriggerKit.AzureClient(forwarder, senders);

        await client.SendToConsumerAsync(Order());

        Assert.Equal(1, calls);
        Assert.Single(_original.Sent);
    }

    [Fact]
    public async Task C7_OriginalFailureStillPropagatesAndNothingIsForwarded()
    {
        _original.Throw = new ServiceBusException("boom", ServiceBusFailureReason.ServiceBusy);
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out var store);

        var ex = await Assert.ThrowsAsync<ServiceBusException>(() => client.SendToConsumerAsync(Order()));

        Assert.Same(_original.Throw, ex);
        Assert.Empty(_trigger.Sent);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task C8_OversizeForwardIsDroppedWithASizeWarning()
    {
        var logger = new RecordingLogger<BlocksEventTriggerForwarder>();
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, logger);

        await client.SendToConsumerAsync(new ConsumerMessage<OrderPlaced>
        {
            ConsumerName = Queue,
            Payload = new OrderPlaced { OrderId = new string('a', 200_000), Secret = TriggerKit.PayloadMarker }
        });

        Assert.Single(_original.Sent);
        Assert.Empty(_trigger.Sent);
        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains("T1", warning.Message);
        Assert.Contains("OrderPlaced", warning.Message);
        var size = (int)warning.State.Single(kv => kv.Key == "Size").Value!;
        Assert.True(size > BlocksEventTriggerConstants.MaxForwardBytes);
        Assert.DoesNotContain(TriggerKit.PayloadMarker, logger.AllText());
        Assert.DoesNotContain(new string('a', 100), logger.AllText());
    }

    [Fact]
    public async Task C8_LimitIsMeasuredOnTheFinalForwardNotThePayload()
    {
        // Each '<' becomes "\\u003C" in Body, then "\\\\u003C" in the forward envelope: a payload under the
        // cap still produces a forward over it.
        var logger = new RecordingLogger<BlocksEventTriggerForwarder>();
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _, logger);
        var payload = new OrderPlaced { OrderId = new string('<', 30_000) };
        Assert.True(JsonSerializer.Serialize(payload).Length < BlocksEventTriggerConstants.MaxForwardBytes);

        await client.SendToConsumerAsync(new ConsumerMessage<OrderPlaced> { ConsumerName = Queue, Payload = payload });

        Assert.Empty(_trigger.Sent);
        Assert.Single(logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task C8_ForwardJustUnderTheLimitIsSent()
    {
        var client = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);

        await client.SendToConsumerAsync(new ConsumerMessage<OrderPlaced>
        {
            ConsumerName = Queue,
            Payload = new OrderPlaced { OrderId = new string('a', 150_000) }
        });

        var forward = Assert.Single(_trigger.Sent);
        Assert.True(forward.Body.ToArray().Length <= BlocksEventTriggerConstants.MaxForwardBytes);
    }

    [Fact]
    public async Task C9_OneTenantsCatalogNeverDecidesAnothers()
    {
        var store = TriggerKit.Store(tenant => tenant == "T1" ? TriggerKit.Catalog(("OrderPlaced", true)) : null);
        var client = TriggerKit.AzureClient(TriggerKit.Forwarder(TriggerKit.Cache(store.Object)), Senders());

        TriggerKit.UseTenant("T1");
        await client.SendToConsumerAsync(Order());
        TriggerKit.UseTenant("T2");
        await client.SendToConsumerAsync(Order());

        var forward = Assert.Single(_trigger.Sent);
        Assert.Equal("T1", forward.ApplicationProperties["TenantId"]);
        Assert.Equal(2, _original.Sent.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoOriginalSendFailure_WhenTheCacheItselfThrows(bool synchronously)
    {
        var logger = new RecordingLogger<BlocksEventTriggerForwarder>();
        var forwarder = TriggerKit.Forwarder(new ThrowingCatalogCache { Synchronous = synchronously }, logger);
        var client = TriggerKit.AzureClient(forwarder, Senders());

        await client.SendToConsumerAsync(Order());

        Assert.Single(_original.Sent);
        Assert.Empty(_trigger.Sent);
        Assert.Single(logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task NoOriginalSendFailure_WhenTheCatalogReadAndRedisBothFail()
    {
        var store = new Mock<IKeyValueStore>();
        store.Setup(s => s.GetAsync<BsonArray>(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());
        var redis = new Mock<ICacheClient>();
        redis.Setup(c => c.SubscribeAsync(It.IsAny<string>(), It.IsAny<Action<RedisChannel, RedisValue>>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var client = TriggerKit.AzureClient(TriggerKit.Forwarder(TriggerKit.Cache(store.Object, redis.Object)), Senders());

        await client.SendToConsumerAsync(Order());

        Assert.Single(_original.Sent);
        Assert.Empty(_trigger.Sent);
    }

    [Fact]
    public async Task ZeroIo_OnCacheHits_WithStrictMocksAfterManySends()
    {
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", false)), MockBehavior.Strict);
        var redis = TriggerKit.CacheClient(MockBehavior.Strict);
        var forwarder = TriggerKit.Forwarder(TriggerKit.Cache(store.Object, redis.Object));
        _original.Record = false;
        var client = TriggerKit.AzureClient(forwarder, Senders());

        for (var i = 0; i < 10_000; i++)
        {
            await client.SendToConsumerAsync(Order());
        }

        store.Verify(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()), Times.Once);
        redis.Verify(c => c.SubscribeAsync(BlocksEventTriggerConstants.InvalidationChannel, It.IsAny<Action<RedisChannel, RedisValue>>()), Times.Once);
        store.VerifyNoOtherCalls();
        redis.VerifyNoOtherCalls();
        Assert.Empty(_trigger.Sent);
    }

    [Fact]
    public async Task H8_HundredConcurrentSendsReadTheCatalogOnce()
    {
        var gate = new TaskCompletionSource<BsonArray?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<IKeyValueStore>();
        store.Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var sender = new Mock<ServiceBusSender>();
        sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var senders = new Dictionary<string, ServiceBusSender> { [Queue] = sender.Object, [BlocksEventTriggerConstants.QueueName] = sender.Object };
        var client = TriggerKit.AzureClient(TriggerKit.Forwarder(TriggerKit.Cache(store.Object)), senders);

        var sends = Enumerable.Range(0, 100).Select(_ => Task.Run(() => client.SendToConsumerAsync(Order()))).ToList();
        await Task.Delay(200);
        gate.SetResult(TriggerKit.Catalog(("OrderPlaced", true)));
        await Task.WhenAll(sends);

        store.Verify(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()), Times.Once);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(200));
    }

    [Fact]
    public async Task H11_C14_SpanTagsOutcomeWithoutPayload()
    {
        var sourceName = "bet-azure-" + Guid.NewGuid().ToString("N");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = TriggerKit.Listen(sourceName, stopped);
        using var source = new ActivitySource(sourceName);
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true), ("SmsQueued", false)));
        var client = TriggerKit.AzureClient(TriggerKit.Forwarder(TriggerKit.Cache(store.Object)), Senders(), source);

        await client.SendToConsumerAsync(Order());
        await client.SendToConsumerAsync(new ConsumerMessage<SmsQueued> { ConsumerName = Queue, Payload = new SmsQueued { Text = TriggerKit.PayloadMarker } });
        TriggerKit.UseTenant(null);
        await client.SendToConsumerAsync(Order());

        var producers = stopped.Where(a => a.Kind == ActivityKind.Producer).ToList();
        var forwarded = producers.Single(a => a.GetTagItem("messaging.message_type") as string == "OrderPlaced" && a.GetTagItem("blocks.event_trigger.forwarded") is true);
        Assert.Null(forwarded.GetTagItem("blocks.event_trigger.skip_reason"));

        var notEnabled = producers.Single(a => a.GetTagItem("messaging.message_type") as string == "SmsQueued");
        Assert.Equal(false, notEnabled.GetTagItem("blocks.event_trigger.forwarded"));
        Assert.Equal("not_enabled", notEnabled.GetTagItem("blocks.event_trigger.skip_reason"));

        Assert.Contains(producers, a => "no_tenant".Equals(a.GetTagItem("blocks.event_trigger.skip_reason")));

        // The forward's own span is a child of the original's and is not tagged with an outcome.
        var forwardSpan = producers.Single(a => a.GetTagItem("messaging.message_type") as string == nameof(BlocksEventTriggerMessage));
        Assert.Equal(forwarded.SpanId, forwardSpan.ParentSpanId);
        Assert.Null(forwardSpan.GetTagItem("blocks.event_trigger.forwarded"));

        var allTags = string.Join("|", producers.SelectMany(a => a.TagObjects).Select(t => $"{t.Key}={t.Value}"));
        Assert.DoesNotContain(TriggerKit.PayloadMarker, allTags);
        Assert.DoesNotContain("oauth-token-secret", allTags);
    }

    [Fact]
    public async Task H11_SkipReasonsForTooLargeRecursionAndFailure()
    {
        var sourceName = "bet-azure-" + Guid.NewGuid().ToString("N");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = TriggerKit.Listen(sourceName, stopped);
        using var source = new ActivitySource(sourceName);
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true), (nameof(BlocksEventTriggerMessage), true)));
        var client = TriggerKit.AzureClient(TriggerKit.Forwarder(TriggerKit.Cache(store.Object)), Senders(), source);

        await client.SendToConsumerAsync(new ConsumerMessage<OrderPlaced> { ConsumerName = Queue, Payload = new OrderPlaced { OrderId = new string('a', 200_000) } });
        await client.SendToConsumerAsync(new ConsumerMessage<BlocksEventTriggerMessage> { ConsumerName = BlocksEventTriggerConstants.QueueName, Payload = new BlocksEventTriggerMessage() });
        _trigger.Throw = new TimeoutException();
        await client.SendToConsumerAsync(Order());

        var reasons = stopped.Select(a => a.GetTagItem("blocks.event_trigger.skip_reason") as string).Where(r => r is not null).ToList();
        Assert.Equal(["too_large", "recursion", "failed"], reasons);
    }

    [Fact]
    public async Task C13_OriginalEnvelopeAndHeadersAreIdenticalWithTheHookOnOrOff()
    {
        var offSender = new RecordingSender(Queue);
        var off = TriggerKit.AzureClient(null, new Dictionary<string, ServiceBusSender> { [Queue] = offSender });
        var on = Client(TriggerKit.Catalog(("OrderPlaced", true)), out _);
        var message = Order() with { DelegationTtl = TimeSpan.FromHours(1) };

        await off.SendToConsumerAsync(message);
        await on.SendToConsumerAsync(message);

        var before = Assert.Single(offSender.Sent);
        var after = Assert.Single(_original.Sent);
        Assert.Equal(before.Body.ToArray(), after.Body.ToArray());
        Assert.Equal(before.ApplicationProperties.Keys.OrderBy(k => k), after.ApplicationProperties.Keys.OrderBy(k => k));
        foreach (var key in before.ApplicationProperties.Keys)
        {
            Assert.Equal(before.ApplicationProperties[key], after.ApplicationProperties[key]);
        }

        Assert.Single(_trigger.Sent);
    }

    [Fact]
    public async Task C13_ScheduledOriginalIsScheduledIdenticallyWithTheHookOn()
    {
        var offSender = new RecordingSender(Queue);
        var off = TriggerKit.AzureClient(null, new Dictionary<string, ServiceBusSender> { [Queue] = offSender });
        var on = Client(TriggerKit.Catalog(("OrderPlaced", false)), out _);
        var message = Order() with { ScheduledEnqueueTimeUtc = DateTimeOffset.UtcNow.AddHours(1) };

        await off.SendToConsumerAsync(message);
        await on.SendToConsumerAsync(message);

        Assert.Equal(offSender.Scheduled.Single().At, _original.Scheduled.Single().At);
        Assert.Equal(offSender.Scheduled.Single().Message.Body.ToArray(), _original.Scheduled.Single().Message.Body.ToArray());
        Assert.Empty(_original.Sent);
    }

    [Fact]
    public void Q2_ExistingConstructorKeepsTheHookOff()
    {
        var client = TriggerKit.AzureClient(null, Senders());

        var field = typeof(AzureMessageClient).GetField("_forwarder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Null(field.GetValue(client));
    }
}
