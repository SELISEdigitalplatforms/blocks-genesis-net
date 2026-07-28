using Blocks.Genesis;
using Moq;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Cache;

public class RedisClientBranchCoverageTests
{
    [Fact]
    public async Task AllOperations_ShouldTagActivities_WhenListenerIsActive()
    {
        var sourceName = $"redis-branch-{Guid.NewGuid():N}";
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var db = new Mock<IDatabase>();
        db.Setup(d => d.KeyExists(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns("value");
        db.Setup(d => d.KeyDelete(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.HashGetAll(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns([]);
        db.Setup(d => d.KeyExpire(It.IsAny<RedisKey>(), It.IsAny<DateTime?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<DateTime?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync("value");
        db.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync([]);

        var sub = new Mock<ISubscriber>();
        sub.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>())).ReturnsAsync(1);

        Action<RedisChannel, RedisValue>? captured = null;
        sub.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
           .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, _) => captured = handler)
           .Returns(Task.CompletedTask);
        sub.Setup(s => s.UnsubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
           .Returns(Task.CompletedTask);

        var client = CreateClient(db, sub, sourceName);
        var entries = new[] { new HashEntry("f", "v") };

        using (var parent = new Activity("redis-parent").Start())
        {
            Assert.True(client.KeyExists("k"));
        }

        Assert.True(client.AddStringValue("k", "value"));
        Assert.True(client.AddStringValue("k", null!));
        Assert.True(client.AddStringValue("k", null!, 30));
        Assert.True(client.AddStringValue("k", "value", 30));

        Assert.Equal("value", client.GetStringValue("k"));
        Assert.True(client.RemoveKey("k"));
        Assert.True(client.AddHashValue("k", entries));
        Assert.True(client.AddHashValue("k", entries, 30));
        Assert.Empty(client.GetHashValue("k"));

        Assert.True(await client.KeyExistsAsync("k"));
        Assert.True(await client.AddStringValueAsync("k", "value"));
        Assert.True(await client.AddStringValueAsync("k", null!));
        Assert.True(await client.AddStringValueAsync("k", "value", 30));
        Assert.True(await client.AddStringValueAsync("k", null!, 30));

        Assert.Equal("value", await client.GetStringValueAsync("k"));
        Assert.True(await client.RemoveKeyAsync("k"));
        Assert.True(await client.AddHashValueAsync("k", entries));
        Assert.True(await client.AddHashValueAsync("k", entries, 30));
        Assert.Empty(await client.GetHashValueAsync("k"));

        Assert.Equal(1, await client.PublishAsync("chan", "message"));
        Assert.Equal(1, await client.PublishAsync("chan", null!));

        var received = new List<RedisValue>();
        await client.SubscribeAsync("chan", (_, value) => received.Add(value));
        Assert.NotNull(captured);
        captured!(RedisChannel.Literal("chan"), "payload");
        Assert.Single(received);

        // A handler that throws is swallowed by the message pump wrapper.
        await client.SubscribeAsync("chan-throws", (_, _) => throw new InvalidOperationException("handler boom"));
        var pumpException = Record.Exception(() => captured!(RedisChannel.Literal("chan-throws"), "payload"));
        Assert.Null(pumpException);

        await client.UnsubscribeAsync("chan");
    }

    [Fact]
    public async Task PubSubFailures_ShouldTagErrorAndRethrow_WhenListenerIsActive()
    {
        var sourceName = $"redis-branch-err-{Guid.NewGuid():N}";
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var db = new Mock<IDatabase>();
        var sub = new Mock<ISubscriber>();
        sub.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
           .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "publish down"));
        sub.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
           .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "subscribe down"));
        sub.Setup(s => s.UnsubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
           .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "unsubscribe down"));

        var client = CreateClient(db, sub, sourceName);

        await Assert.ThrowsAsync<RedisConnectionException>(() => client.PublishAsync("chan", "m"));
        await Assert.ThrowsAsync<RedisConnectionException>(() => client.SubscribeAsync("chan", (_, _) => { }));
        await Assert.ThrowsAsync<RedisConnectionException>(() => client.UnsubscribeAsync("chan"));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.PublishAsync("", "m"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SubscribeAsync("", (_, _) => { }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SubscribeAsync("chan", null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.UnsubscribeAsync(""));
    }

    private static RedisClient CreateClient(Mock<IDatabase> db, Mock<ISubscriber> sub, string sourceName)
    {
        var client = (RedisClient)RuntimeHelpers.GetUninitializedObject(typeof(RedisClient));
        SetField(client, "_database", db.Object);
        SetField(client, "_subscriber", sub.Object);
        SetField(client, "_activitySource", new ActivitySource(sourceName));
        SetField(client, "_subscriptions", new ConcurrentDictionary<string, Action<RedisChannel, RedisValue>>());
        return client;
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }
}
