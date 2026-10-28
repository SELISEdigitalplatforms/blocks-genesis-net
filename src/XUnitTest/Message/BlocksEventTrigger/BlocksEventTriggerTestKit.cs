using Azure.Messaging.ServiceBus;
using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Moq;
using RabbitMQ.Client;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using XUnitTest.Delegation;

namespace XUnitTest.Message.BlocksEventTrigger;

/// <summary>A manual clock. Its timers fire only when <see cref="Advance"/> moves time past their due time.</summary>
internal sealed class TriggerClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Timers created and not yet disposed or fired.</summary>
    public int PendingTimers
    {
        get { lock (_timers) { return _timers.Count(t => t.DueAt is not null); } }
    }

    public void Advance(TimeSpan by)
    {
        Now += by;
        List<ManualTimer> due;
        lock (_timers)
        {
            due = _timers.Where(t => t.DueAt <= Now).ToList();
            foreach (var timer in due)
            {
                timer.DueAt = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(TriggerClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._timers)
            {
                DueAt = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record LogEntry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception);

/// <summary>Captures every log call, including its structured state, so tests can assert what is (not) logged.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), values.ToList(), exception));
    }

    public IReadOnlyList<LogEntry> At(LogLevel level) => Entries.Where(e => e.Level == level).ToList();

    /// <summary>Every rendered message, structured value and exception message, for "never logged" checks.</summary>
    public string AllText() => string.Join("\n", Entries.Select(e =>
        e.Message + "|" + string.Join("|", e.State.Select(kv => $"{kv.Key}={kv.Value}")) + "|" + e.Exception?.Message));
}

/// <summary>A no-I/O <see cref="ServiceBusSender"/> that records what it was asked to send.</summary>
internal sealed class RecordingSender : ServiceBusSender
{
    private readonly string _name;

    public RecordingSender(string name) => _name = name;

    public override string EntityPath => _name;

    public List<ServiceBusMessage> Sent { get; } = [];

    public List<(ServiceBusMessage Message, DateTimeOffset At)> Scheduled { get; } = [];

    public Exception? Throw { get; set; }

    public bool Record { get; set; } = true;

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        if (Throw is not null)
        {
            return Task.FromException(Throw);
        }

        if (Record)
        {
            Sent.Add(message);
        }

        return Task.CompletedTask;
    }

    public override Task<long> ScheduleMessageAsync(ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        if (Throw is not null)
        {
            return Task.FromException<long>(Throw);
        }

        Scheduled.Add((message, scheduledEnqueueTime));
        return Task.FromResult(1L);
    }
}

/// <summary>A third-party forwarder that breaks its "never throws" contract.</summary>
internal sealed class ThrowingForwarder : IBlocksEventTriggerForwarder
{
    public bool Synchronous { get; init; }

    public int Calls;

    public ValueTask TryForwardAsync<T>(
        ConsumerMessage<T> original,
        string wireType,
        string payloadJson,
        string? tenantId,
        bool isMassConsumer,
        Activity? producerActivity,
        Func<ConsumerMessage<BlocksEventTriggerMessage>, string, Task> sendForward) where T : class
    {
        Interlocked.Increment(ref Calls);
        var ex = new InvalidOperationException("forwarder broken " + TriggerKit.PayloadMarker);
        return Synchronous ? throw ex : ValueTask.FromException(ex);
    }
}

/// <summary>A cache whose lookup always fails, to prove a broken cache never fails the original send.</summary>
internal sealed class ThrowingCatalogCache : IBlocksEventCatalogCache
{
    public bool Synchronous { get; init; }

    public ValueTask<System.Collections.Frozen.FrozenSet<string>> GetEnabledEventsAsync(string tenantId) =>
        Synchronous
            ? throw new InvalidOperationException("cache broken")
            : ValueTask.FromException<System.Collections.Frozen.FrozenSet<string>>(new InvalidOperationException("cache broken"));
}

public sealed class OrderPlaced
{
    public string OrderId { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
}

public sealed class SmsQueued
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>Builders and a tenant-scoped <see cref="BlocksContext"/> for the Blocks Event Trigger tests.</summary>
internal static class TriggerKit
{
    public const string ValidAzureConnection = "Endpoint=sb://unit-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=01234567890123456789012345678901234567890123456789=";
    public const string ServiceName = "blocks-iam";
    public const string PayloadMarker = "PAYLOAD-MARKER-7f3c";

    public static BsonDocument Entry(string eventName, bool workflow, bool function = false) => new()
    {
        ["EventName"] = eventName,
        ["Label"] = eventName + " label",
        ["Description"] = "desc",
        ["ServiceKey"] = "iam",
        ["SourceServiceKeys"] = new BsonArray { "iam" },
        ["ConsumerNames"] = new BsonArray { "blocks_iam_listener_user" },
        ["IsValidForWorkflow"] = workflow,
        ["IsValidForFunction"] = function
    };

    public static BsonArray Catalog(params (string Name, bool Workflow)[] entries) =>
        new(entries.Select(e => Entry(e.Name, e.Workflow)));

    /// <summary>A store whose catalog depends on the tenant in the current <see cref="BlocksContext"/>, as an impersonated read does.</summary>
    public static Mock<IKeyValueStore> Store(Func<string?, BsonArray?> catalogForTenant, MockBehavior behavior = MockBehavior.Default)
    {
        var store = new Mock<IKeyValueStore>(behavior);
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(catalogForTenant(BlocksContext.GetContext()?.TenantId)));
        return store;
    }

    public static Mock<IKeyValueStore> Store(BsonArray? catalog, MockBehavior behavior = MockBehavior.Default) =>
        Store(_ => catalog, behavior);

    public static Mock<ICacheClient> CacheClient(MockBehavior behavior = MockBehavior.Default) =>
        CacheClient(out _, behavior);

    public static Mock<ICacheClient> CacheClient(out Func<Action<RedisChannel, RedisValue>?> handler, MockBehavior behavior = MockBehavior.Default)
    {
        Action<RedisChannel, RedisValue>? captured = null;
        var cache = new Mock<ICacheClient>(behavior);
        cache
            .Setup(c => c.SubscribeAsync(BlocksEventTriggerConstants.InvalidationChannel, It.IsAny<Action<RedisChannel, RedisValue>>()))
            .Callback<string, Action<RedisChannel, RedisValue>>((_, h) => captured = h)
            .Returns(Task.CompletedTask);
        handler = () => captured;
        return cache;
    }

    public static BlocksEventCatalogCache Cache(
        IKeyValueStore store,
        ICacheClient? cacheClient = null,
        TimeProvider? clock = null,
        ILogger<BlocksEventCatalogCache>? logger = null) =>
        new(store, cacheClient ?? CacheClient().Object, clock ?? new TriggerClock(), logger ?? new RecordingLogger<BlocksEventCatalogCache>());

    public static BlocksEventTriggerForwarder Forwarder(
        IBlocksEventCatalogCache cache,
        ILogger<BlocksEventTriggerForwarder>? logger = null,
        TimeProvider? clock = null,
        string? serviceName = ServiceName) =>
        new(cache, new MessageConfiguration { ServiceName = serviceName }, clock ?? new TriggerClock(), logger ?? new RecordingLogger<BlocksEventTriggerForwarder>());

    public static BlocksContext Context(string? tenantId) => BlocksContext.Create(
        tenantId: tenantId,
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
        oauthToken: "oauth-token-secret",
        originalTenantId: tenantId);

    /// <summary>Puts <paramref name="tenantId"/> in the current flow's <see cref="BlocksContext"/> (test mode).</summary>
    public static void UseTenant(string? tenantId)
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(tenantId is null ? null : Context(tenantId));
    }

    public static void ClearTenant()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
    }

    public static AzureMessageClient AzureClient(
        IBlocksEventTriggerForwarder? forwarder,
        IDictionary<string, ServiceBusSender> senders,
        ActivitySource? activitySource = null,
        IDelegationGrantFactory? grantFactory = null,
        ILogger<AzureMessageClient>? logger = null)
    {
        var configuration = new MessageConfiguration
        {
            Connection = ValidAzureConnection,
            ServiceName = ServiceName,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration { Queues = [], Topics = [] }
        };

        var source = activitySource ?? new ActivitySource("blocks-event-trigger-tests");
        var grants = grantFactory ?? DelegationTestDoubles.NoGrantFactory();
        var client = forwarder is null
            ? new AzureMessageClient(configuration, source, grants)
            : new AzureMessageClient(configuration, source, grants, forwarder, logger);

        // Originals use _senders; forwards use _forwardSenders (the no-retry client). Each gets every sender.
        foreach (var fieldName in new[] { "_senders", "_forwardSenders" })
        {
            var field = typeof(AzureMessageClient).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var map = (ConcurrentDictionary<string, ServiceBusSender>)field.GetValue(client)!;
            foreach (var (name, sender) in senders)
            {
                map[name] = sender;
            }
        }

        return client;
    }

    public static Mock<IChannel> Channel()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        return channel;
    }

    public static RabbitMessageClient RabbitClient(
        IBlocksEventTriggerForwarder? forwarder,
        IChannel channel,
        ILogger<RabbitMessageClient>? logger = null,
        ActivitySource? activitySource = null,
        IDelegationGrantFactory? grantFactory = null)
    {
        var rabbit = new Mock<IRabbitMqService>();
        rabbit.Setup(r => r.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbit.SetupGet(r => r.RabbitMqChannel).Returns(channel);

        var configuration = new MessageConfiguration
        {
            Connection = "amqp://localhost:5672",
            ServiceName = ServiceName,
            RabbitMqConfiguration = new RabbitMqConfiguration()
        };

        var log = logger ?? new RecordingLogger<RabbitMessageClient>();
        var source = activitySource ?? new ActivitySource("blocks-event-trigger-tests");
        var grants = grantFactory ?? DelegationTestDoubles.NoGrantFactory();

        return forwarder is null
            ? new RabbitMessageClient(log, rabbit.Object, configuration, source, grants)
            : new RabbitMessageClient(log, rabbit.Object, configuration, source, grants, forwarder);
    }

    public static ActivityListener Listen(string sourceName, ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Waits (up to 5 s of wall time) for work a fake timer started on another thread.</summary>
    public static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 5 s");
            await Task.Delay(5);
        }
    }

    /// <summary>Reads the trigger message out of a serialized forward envelope.</summary>
    public static BlocksEventTriggerMessage ReadForward(string envelopeJson)
    {
        var envelope = System.Text.Json.JsonSerializer.Deserialize<Blocks.Genesis.Message>(envelopeJson)!;
        Assert.Equal(nameof(BlocksEventTriggerMessage), envelope.Type);
        return System.Text.Json.JsonSerializer.Deserialize<BlocksEventTriggerMessage>(envelope.Body)!;
    }
}
