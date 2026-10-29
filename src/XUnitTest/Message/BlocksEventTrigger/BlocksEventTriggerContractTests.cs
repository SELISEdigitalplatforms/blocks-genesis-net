using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Collections.Frozen;
using System.Text.Json;

namespace XUnitTest.Message.BlocksEventTrigger;

/// <summary>The values and shapes blocks-logic#375 consumes.</summary>
public sealed class BlocksEventTriggerContractTests
{
    [Fact]
    public void Constants_MatchTheSharedContract()
    {
        Assert.Equal("blocks_logic_workflow_blocks_event_trigger_listener", BlocksEventTriggerConstants.QueueName);
        Assert.Equal("blocks-event-triggers", BlocksEventTriggerConstants.CatalogKey);
        Assert.Equal("blocks-event-triggers::updates", BlocksEventTriggerConstants.InvalidationChannel);
        Assert.Equal(TimeSpan.FromMinutes(5), BlocksEventTriggerConstants.CacheLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), BlocksEventTriggerConstants.FailureCacheLifetime);
        Assert.Equal(196_608, BlocksEventTriggerConstants.MaxForwardBytes);
        Assert.Equal(10_000, BlocksEventTriggerConstants.MaxCachedTenants);
    }

    [Fact]
    public void Contracts_LiveInTheBlocksGenesisNamespace()
    {
        Assert.Equal("Blocks.Genesis", typeof(BlocksEventTriggerConstants).Namespace);
        Assert.Equal("Blocks.Genesis", typeof(BlocksEventCatalogEntry).Namespace);
        Assert.Equal("Blocks.Genesis", typeof(BlocksEventTriggerMessage).Namespace);
        Assert.Equal("Blocks.Genesis", typeof(IBlocksEventTriggerForwarder).Namespace);
        Assert.True(typeof(IBlocksEventTriggerForwarder).IsPublic);
        Assert.False(typeof(BlocksEventTriggerForwarder).IsPublic);
    }

    [Fact]
    public void TriggerMessage_HasTheContractFieldsAndRoundTrips()
    {
        var message = new BlocksEventTriggerMessage
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventName = "UserMutationEvent",
            SourceService = "blocks-iam",
            ConsumerName = "blocks_iam_listener_user",
            IsMassConsumer = true,
            Timestamp = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc),
            PayloadJson = "{\"a\":1}"
        };

        var json = JsonSerializer.Serialize(message);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            ["EventId", "EventName", "SourceService", "ConsumerName", "IsMassConsumer", "Timestamp", "PayloadJson"],
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.EndsWith("Z", doc.RootElement.GetProperty("Timestamp").GetString());

        var back = JsonSerializer.Deserialize<BlocksEventTriggerMessage>(json)!;
        Assert.Equal(message.EventId, back.EventId);
        Assert.Equal(message.PayloadJson, back.PayloadJson);
        Assert.Equal(DateTimeKind.Utc, back.Timestamp.Kind);
    }

    [Fact]
    public void CatalogEntry_RoundTripsThroughBsonAndIgnoresExtraElements()
    {
        var entry = new BlocksEventCatalogEntry
        {
            EventName = "UserMutationEvent",
            Label = "User changed",
            Description = "d",
            ServiceKey = "iam",
            SourceServiceKeys = ["iam"],
            ConsumerNames = ["blocks_iam_listener_user"],
            IsValidForWorkflow = true,
            IsValidForFunction = false
        };

        var document = entry.ToBsonDocument();
        Assert.Equal(
            ["EventName", "Label", "Description", "ServiceKey", "SourceServiceKeys", "ConsumerNames", "IsValidForWorkflow", "IsValidForFunction"],
            document.Names.ToArray());

        document["AddedLater"] = "x";
        var back = BsonSerializer.Deserialize<BlocksEventCatalogEntry>(document);
        Assert.Equal("UserMutationEvent", back.EventName);
        Assert.True(back.IsValidForWorkflow);
    }

    [Fact]
    public void CatalogEntry_DefaultsAreEmpty()
    {
        var entry = new BlocksEventCatalogEntry();

        Assert.Equal(string.Empty, entry.EventName);
        Assert.Equal(string.Empty, entry.Label);
        Assert.Equal(string.Empty, entry.Description);
        Assert.Equal(string.Empty, entry.ServiceKey);
        Assert.Empty(entry.SourceServiceKeys);
        Assert.Empty(entry.ConsumerNames);
        Assert.False(entry.IsValidForWorkflow);
        Assert.False(entry.IsValidForFunction);
    }

    [Fact]
    public async Task Forwarder_WithoutAProducerSpanStillForwards()
    {
        var forwarder = TriggerKit.Forwarder(new FixedCache(["OrderPlaced"]));
        string? sent = null;

        await forwarder.TryForwardAsync(
            new ConsumerMessage<OrderPlaced> { ConsumerName = "q", Payload = new OrderPlaced() },
            "OrderPlaced", "{}", "T1", isMassConsumer: false, producerActivity: null,
            (_, envelope) => { sent = envelope; return Task.CompletedTask; });

        Assert.Equal("{}", TriggerKit.ReadForward(sent!).PayloadJson);
    }

    [Fact]
    public async Task Forwarder_AwaitsACacheThatCompletesLater()
    {
        var forwarder = TriggerKit.Forwarder(new FixedCache(["OrderPlaced"]) { Delay = true });
        var sends = 0;

        await forwarder.TryForwardAsync(
            new ConsumerMessage<OrderPlaced> { ConsumerName = "q", Payload = new OrderPlaced() },
            "OrderPlaced", "{}", "T1", isMassConsumer: false, producerActivity: null,
            (_, _) => { sends++; return Task.CompletedTask; });

        Assert.Equal(1, sends);
    }

    private sealed class FixedCache(string[] enabled) : IBlocksEventCatalogCache
    {
        public bool Delay { get; init; }

        public async ValueTask<FrozenSet<string>> GetEnabledEventsAsync(string tenantId)
        {
            if (Delay)
            {
                await Task.Yield();
            }

            return enabled.ToFrozenSet(StringComparer.Ordinal);
        }
    }
}
