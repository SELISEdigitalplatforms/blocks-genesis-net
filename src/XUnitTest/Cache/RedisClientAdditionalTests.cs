using Blocks.Genesis;
using Moq;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Cache;

public class RedisClientAdditionalTests
{
    [Fact]
    public void AllSyncOperations_ShouldSetActivityTags_WhenListenerIsAttached()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var db = new Mock<IDatabase>();
        db.Setup(d => d.KeyExists(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns((RedisValue)"v");
        db.Setup(d => d.KeyDelete(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.KeyExpire(It.IsAny<RedisKey>(), It.IsAny<DateTime?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.HashGetAll(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns([new HashEntry("a", "1")]);

        var client = CreateClient(db, new Mock<ISubscriber>(), "redis-additional-sync");

        Assert.True(client.KeyExists("k1"));
        Assert.True(client.AddStringValue("k2", "value"));
        Assert.True(client.AddStringValue("k3", "value", 60));
        Assert.Equal("v", client.GetStringValue("k4"));
        Assert.True(client.RemoveKey("k5"));
        Assert.True(client.AddHashValue("k6", [new HashEntry("a", "1")]));
        Assert.True(client.AddHashValue("k7", [new HashEntry("a", "1")], 45));
        Assert.Single(client.GetHashValue("k8"));

        // Each operation should have produced one activity with the Key tag set.
        Assert.Contains(activities, a => a.DisplayName.EndsWith("KeyExists") && GetTag(a, "Key") == "k1" && GetTag(a, "Exists") == "True");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddStringValue") && GetTag(a, "ValueLength") == "5" && GetTag(a, "Success") == "True");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddStringValueWithTTL") && GetTag(a, "TTLSeconds") == "60" && GetTag(a, "TTLSetSuccess") == "True");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("GetStringValue") && GetTag(a, "Hit") == "True");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("RemoveKey") && GetTag(a, "Removed") == "True");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddHashValue") && GetTag(a, "HashFieldCount") == "1");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddHashValueWithTTL") && GetTag(a, "TTLSeconds") == "45");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("GetHashValue") && GetTag(a, "FieldCount") == "1");
    }

    [Fact]
    public async Task AllAsyncOperations_ShouldSetActivityTags_WhenListenerIsAttached()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var db = new Mock<IDatabase>();
        db.Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync((RedisValue)"v");
        db.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<DateTime?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>())).Returns(Task.CompletedTask);
        db.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync([new HashEntry("a", "1")]);

        var client = CreateClient(db, new Mock<ISubscriber>(), "redis-additional-async");

        Assert.True(await client.KeyExistsAsync("k1"));
        Assert.True(await client.AddStringValueAsync("k2", "value"));
        Assert.True(await client.AddStringValueAsync("k3", "value", 90));
        Assert.Equal("v", await client.GetStringValueAsync("k4"));
        Assert.True(await client.RemoveKeyAsync("k5"));
        Assert.True(await client.AddHashValueAsync("k6", [new HashEntry("a", "1")]));
        Assert.True(await client.AddHashValueAsync("k7", [new HashEntry("a", "1")], 90));
        Assert.Single(await client.GetHashValueAsync("k8"));

        Assert.Contains(activities, a => a.DisplayName.EndsWith("KeyExistsAsync") && GetTag(a, "Exists") == "True");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddStringValueAsync") && GetTag(a, "ValueLength") == "5");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddStringValueWithTTLAsync") && GetTag(a, "TTLSeconds") == "90");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("GetStringValueAsync") && GetTag(a, "Hit") == "True");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("RemoveKeyAsync") && GetTag(a, "Removed") == "True");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddHashValueAsync") && GetTag(a, "HashFieldCount") == "1");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddHashValueWithTTLAsync") && GetTag(a, "TTLSeconds") == "90");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("GetHashValueAsync") && GetTag(a, "FieldCount") == "1");
    }

    [Fact]
    public void AddStringValue_ShouldUseZeroLength_WhenValueIsNull()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>())).Returns(true);
        db.Setup(d => d.KeyExpire(It.IsAny<RedisKey>(), It.IsAny<DateTime?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>())).Returns(true);

        var client = CreateClient(db, new Mock<ISubscriber>(), "redis-null-value");

        Assert.True(client.AddStringValue("k", null!));
        Assert.True(client.AddStringValue("k", null!, 60));

        // null-coalescing branch: value?.Length ?? 0  →  0
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddStringValue") && GetTag(a, "ValueLength") == "0");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddStringValueWithTTL") && GetTag(a, "ValueLength") == "0");
    }

    [Fact]
    public async Task AddStringValueAsync_ShouldUseZeroLength_WhenValueIsNull()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<DateTime?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);

        var client = CreateClient(db, new Mock<ISubscriber>(), "redis-null-value-async");

        Assert.True(await client.AddStringValueAsync("k", null!));
        Assert.True(await client.AddStringValueAsync("k", null!, 60));

        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddStringValueAsync") && GetTag(a, "ValueLength") == "0");
        Assert.Contains(activities, a => a.DisplayName.EndsWith("AddStringValueWithTTLAsync") && GetTag(a, "ValueLength") == "0");
    }

    [Fact]
    public async Task PublishAsync_ShouldUseZeroLength_WhenMessageIsNull()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var sub = new Mock<ISubscriber>();
        sub.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>())).ReturnsAsync(0);

        var client = CreateClient(new Mock<IDatabase>(), sub, "redis-publish-null");

        var result = await client.PublishAsync("ch", null!);

        Assert.Equal(0, result);
        Assert.Contains(activities, a => a.DisplayName.EndsWith("Publish") && GetTag(a, "MessageLength") == "0" && GetTag(a, "SubscribersNotified") == "0");
    }

    [Fact]
    public async Task PublishAsync_ShouldSetErrorTags_WhenSubscriberThrows()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var sub = new Mock<ISubscriber>();
        sub.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new InvalidOperationException("publish-err"));

        var client = CreateClient(new Mock<IDatabase>(), sub, "redis-publish-err");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PublishAsync("ch", "msg"));

        var activity = Assert.Single(activities, a => a.DisplayName.EndsWith("Publish"));
        Assert.Equal("True", GetTag(activity, "Error"));
        Assert.Equal("publish-err", GetTag(activity, "errorMessage"));
    }

    [Fact]
    public async Task SubscribeAsync_InnerHandlerInvocation_ShouldTagMessageActivity_OnSuccessAndError()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var sub = new Mock<ISubscriber>();
        Action<RedisChannel, RedisValue>? captured = null;
        sub.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, cb, _) => captured = cb)
            .Returns(Task.CompletedTask);

        var client = CreateClient(new Mock<IDatabase>(), sub, "redis-sub-inner");

        var callCount = 0;
        await client.SubscribeAsync("ch", (_, _) =>
        {
            callCount++;
            if (callCount == 2) throw new InvalidOperationException("handler-err");
        });

        captured!.Invoke("ch", "payload-1");
        captured!.Invoke("ch", "payload-2");

        var messageActivities = activities.Where(a => a.DisplayName == "Redis::MessageReceived").ToList();
        Assert.Equal(2, messageActivities.Count);
        Assert.All(messageActivities, a => Assert.Equal("ch", GetTag(a, "Channel")));
        // Second invocation must record the error tags.
        Assert.Contains(messageActivities, a => GetTag(a, "Error") == "True" && GetTag(a, "errorMessage") == "handler-err");
    }

    [Fact]
    public async Task SubscribeAsync_ShouldSetErrorTags_WhenSubscriberThrows()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var sub = new Mock<ISubscriber>();
        sub.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new InvalidOperationException("sub-err"));

        var client = CreateClient(new Mock<IDatabase>(), sub, "redis-sub-err");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubscribeAsync("ch", (_, _) => { }));

        var activity = Assert.Single(activities, a => a.DisplayName.EndsWith("Subscribe"));
        Assert.Equal("True", GetTag(activity, "Error"));
        Assert.Equal("sub-err", GetTag(activity, "errorMessage"));
    }

    [Fact]
    public async Task UnsubscribeAsync_ShouldTagUnsubscribedTrue_OnSuccess_AndSetErrorTags_OnFailure()
    {
        var activities = new List<Activity>();
        using var listener = StartCollectingListener(activities);

        var sub = new Mock<ISubscriber>();
        sub.Setup(s => s.UnsubscribeAsync("ok", null, CommandFlags.None)).Returns(Task.CompletedTask);
        sub.Setup(s => s.UnsubscribeAsync("bad", null, CommandFlags.None))
            .ThrowsAsync(new InvalidOperationException("unsub-err"));

        var client = CreateClient(new Mock<IDatabase>(), sub, "redis-unsub");

        await client.UnsubscribeAsync("ok");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UnsubscribeAsync("bad"));

        var unsubActivities = activities.Where(a => a.DisplayName.EndsWith("Unsubscribe")).ToList();
        Assert.Contains(unsubActivities, a => GetTag(a, "Key") == "ok" && GetTag(a, "Unsubscribed") == "True");
        Assert.Contains(unsubActivities, a => GetTag(a, "Key") == "bad" && GetTag(a, "Error") == "True" && GetTag(a, "errorMessage") == "unsub-err");
    }

    // -----------------------------------------------------------------------

    private static ActivityListener StartCollectingListener(List<Activity> collector)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("redis-additional") || source.Name.StartsWith("redis-"),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { },
            ActivityStopped = a => collector.Add(a)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static string? GetTag(Activity activity, string key)
    {
        foreach (var kvp in activity.TagObjects)
        {
            if (kvp.Key == key) return kvp.Value?.ToString();
        }
        return null;
    }

    private static RedisClient CreateClient(Mock<IDatabase> db, Mock<ISubscriber> sub, string activitySourceName)
    {
        var instance = (RedisClient)RuntimeHelpers.GetUninitializedObject(typeof(RedisClient));
        SetField(instance, "_database", db.Object);
        SetField(instance, "_subscriber", sub.Object);
        SetField(instance, "_activitySource", new ActivitySource(activitySourceName));
        SetField(instance, "_subscriptions", new ConcurrentDictionary<string, Action<RedisChannel, RedisValue>>());
        SetField(instance, "_disposed", false);
        return instance;
    }

    private static void SetField(object instance, string fieldName, object? value)
    {
        var field = typeof(RedisClient).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }
}
