using Blocks.Genesis;
using Moq;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Cache;

public class RedisClientScaffoldTests
{
    [Fact]
    public void KeyExists_ShouldReturnTrue_WhenDatabaseHasKey()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.KeyExists("k", CommandFlags.None)).Returns(true);

        var client = CreateClient(db, new Mock<ISubscriber>());

        Assert.True(client.KeyExists("k"));
    }

    [Fact]
    public void AddAndGetAndRemoveString_ShouldCallExpectedDatabaseMethods()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.StringGet("k", CommandFlags.None)).Returns((RedisValue)"v");
        db.Setup(d => d.KeyDelete("k", CommandFlags.None)).Returns(true);

        var client = CreateClient(db, new Mock<ISubscriber>());

        Assert.True(client.AddStringValue("k", "v"));
        Assert.Equal("v", client.GetStringValue("k"));
        Assert.True(client.RemoveKey("k"));
    }

    [Fact]
    public void AddStringValue_WithTtl_ShouldSetValueAndExpire()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringSet(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.KeyExpire(
            It.IsAny<RedisKey>(),
            It.IsAny<DateTime?>(),
            It.IsAny<ExpireWhen>(),
            It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.KeyExpire(
            It.IsAny<RedisKey>(),
            It.IsAny<DateTime>(),
            It.IsAny<ExpireWhen>(),
            It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.KeyExpire(
            It.IsAny<RedisKey>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<ExpireWhen>(),
            It.IsAny<CommandFlags>())).Returns(true);

        var client = CreateClient(db, new Mock<ISubscriber>());

        var result = client.AddStringValue("k", "v", 60);

        Assert.True(result);
        db.Verify(d => d.StringSet(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
        db.Verify(d => d.KeyExpire(
            It.IsAny<RedisKey>(),
            It.IsAny<DateTime?>(),
            It.IsAny<ExpireWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task AsyncMethods_ShouldCallExpectedDatabaseMethods()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.KeyExistsAsync("k", CommandFlags.None)).ReturnsAsync(true);
        db.Setup(d => d.StringSetAsync("k", "v", null, false, When.Always, CommandFlags.None)).ReturnsAsync(true);
        db.Setup(d => d.KeyDeleteAsync("k", CommandFlags.None)).ReturnsAsync(true);
        db.Setup(d => d.HashSetAsync("h", It.IsAny<HashEntry[]>(), CommandFlags.None)).Returns(Task.CompletedTask);
        db.Setup(d => d.HashGetAllAsync("h", CommandFlags.None)).ReturnsAsync([new HashEntry("f", "1")]);

        var client = CreateClient(db, new Mock<ISubscriber>());

        Assert.True(await client.KeyExistsAsync("k"));
        Assert.True(await client.AddStringValueAsync("k", "v"));
        Assert.True(await client.RemoveKeyAsync("k"));
        Assert.True(await client.AddHashValueAsync("h", [new HashEntry("f", "1")]));
        var hash = await client.GetHashValueAsync("h");
        Assert.Single(hash);
    }

    [Fact]
    public async Task GetStringValueAsync_ShouldReturnStoredValue()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringGetAsync("key", CommandFlags.None)).ReturnsAsync((RedisValue)"value");

        var client = CreateClient(db, new Mock<ISubscriber>());

        var result = await client.GetStringValueAsync("key");

        Assert.Equal("value", result);
    }

    [Fact]
    public async Task PublishAsync_ShouldReturnSubscriberCount()
    {
        var sub = new Mock<ISubscriber>();
        sub.Setup(s => s.PublishAsync("channel-1", "hello", CommandFlags.None)).ReturnsAsync(3);

        var client = CreateClient(new Mock<IDatabase>(), sub);

        var result = await client.PublishAsync("channel-1", "hello");

        Assert.Equal(3, result);
    }

    [Fact]
    public async Task SubscribeAndUnsubscribe_ShouldTrackAndRemoveSubscription()
    {
        var sub = new Mock<ISubscriber>();
        var callbackInvoked = false;
        Action<RedisChannel, RedisValue>? captured = null;

        sub.Setup(s => s.SubscribeAsync("updates", It.IsAny<Action<RedisChannel, RedisValue>>(), CommandFlags.None))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, _) => captured = handler)
            .Returns(Task.CompletedTask);

        sub.Setup(s => s.UnsubscribeAsync("updates", null, CommandFlags.None)).Returns(Task.CompletedTask);

        var client = CreateClient(new Mock<IDatabase>(), sub);

        await client.SubscribeAsync("updates", (_, _) => callbackInvoked = true);
        captured?.Invoke("updates", "payload");
        await client.UnsubscribeAsync("updates");

        Assert.True(callbackInvoked);
        sub.Verify(s => s.SubscribeAsync("updates", It.IsAny<Action<RedisChannel, RedisValue>>(), CommandFlags.None), Times.Once);
        sub.Verify(s => s.UnsubscribeAsync("updates", null, CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_ShouldThrow_WhenChannelIsEmpty()
    {
        var client = CreateClient(new Mock<IDatabase>(), new Mock<ISubscriber>());

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.PublishAsync(string.Empty, "msg"));
    }

    [Fact]
    public void Dispose_ShouldUnsubscribeAllTrackedChannels()
    {
        var sub = new Mock<ISubscriber>();
        var client = CreateClient(new Mock<IDatabase>(), sub);

        var subscriptions = GetField<ConcurrentDictionary<string, Action<RedisChannel, RedisValue>>>(client, "_subscriptions");
        subscriptions.TryAdd("updates", (_, _) => { });
        subscriptions.TryAdd("alerts", (_, _) => { });

        client.Dispose();

        sub.Verify(s => s.Unsubscribe(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>?>(), It.IsAny<CommandFlags>()), Times.Exactly(2));
        Assert.Empty(subscriptions);
    }

    private static RedisClient CreateClient(Mock<IDatabase> db, Mock<ISubscriber> sub)
    {
        var instance = (RedisClient)RuntimeHelpers.GetUninitializedObject(typeof(RedisClient));
        SetField(instance, "_database", db.Object);
        SetField(instance, "_subscriber", sub.Object);
        SetField(instance, "_activitySource", new System.Diagnostics.ActivitySource("test-redis"));
        SetField(instance, "_subscriptions", new ConcurrentDictionary<string, Action<RedisChannel, RedisValue>>());
        SetField(instance, "_disposed", false);
        return instance;
    }

    private static void SetField(object instance, string fieldName, object? value)
    {
        var field = typeof(RedisClient).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static T GetField<T>(object instance, string fieldName)
    {
        var field = typeof(RedisClient).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(instance)!;
    }
}
