using Blocks.Genesis;
using MongoDB.Driver.Core.Events;
using System.Diagnostics;
using System.Reflection;

namespace XUnitTest.Database;

public class MongoEventSubscriberTests
{
    [Fact]
    public void Handle_CommandStartedEvent_ShouldCreateActivity()
    {
        using var activitySource = new ActivitySource("test-mongo-events");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-mongo-events",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var subscriber = new MongoEventSubscriber(activitySource);

        Assert.True(subscriber.TryGetEventHandler<CommandStartedEvent>(out var startHandler));
        Assert.NotNull(startHandler);
    }

    [Fact]
    public void TryGetEventHandler_ShouldReturnHandler_ForCommandSucceededEvent()
    {
        var activitySource = new ActivitySource("test-mongo-sub");
        var subscriber = new MongoEventSubscriber(activitySource);

        var found = subscriber.TryGetEventHandler<CommandSucceededEvent>(out var handler);

        Assert.True(found);
        Assert.NotNull(handler);
    }

    [Fact]
    public void TryGetEventHandler_CommandFailed_ShouldReturnHandler()
    {
        var activitySource = new ActivitySource("test-mongo-sub-fail");
        var subscriber = new MongoEventSubscriber(activitySource);

        var found = subscriber.TryGetEventHandler<CommandFailedEvent>(out var handler);

        Assert.True(found);
        Assert.NotNull(handler);
    }

    [Fact]
    public void Handle_CommandStarted_ShouldStartAndTrackActivity()
    {
        using var activitySource = new ActivitySource("test-mongo-handle-start");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-mongo-handle-start",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var subscriber = new MongoEventSubscriber(activitySource);
        subscriber.TryGetEventHandler<CommandStartedEvent>(out var handler);

        var evt = CreateCommandStartedEvent("find", 123);
        handler!(evt);

        // The activity should exist in the internal dictionary (request id defaults to 0 for uninitialized)
        var activities = GetActivitiesField(subscriber);
        Assert.True(activities.ContainsKey(0));
    }

    [Fact]
    public void Handle_CommandSucceeded_ShouldStopAndRemoveActivity()
    {
        using var activitySource = new ActivitySource("test-mongo-handle-success");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-mongo-handle-success",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var subscriber = new MongoEventSubscriber(activitySource);
        subscriber.TryGetEventHandler<CommandStartedEvent>(out var startHandler);
        subscriber.TryGetEventHandler<CommandSucceededEvent>(out var successHandler);

        var startEvt = CreateCommandStartedEvent("insert", 456);
        startHandler!(startEvt);

        var successEvt = CreateCommandSucceededEvent(456, TimeSpan.FromMilliseconds(50));
        successHandler!(successEvt);

        var activities = GetActivitiesField(subscriber);
        Assert.False(activities.ContainsKey(0));
    }

    [Fact]
    public void Handle_CommandFailed_ShouldStopAndRemoveActivity()
    {
        using var activitySource = new ActivitySource("test-mongo-handle-failure");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-mongo-handle-failure",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var subscriber = new MongoEventSubscriber(activitySource);
        subscriber.TryGetEventHandler<CommandStartedEvent>(out var startHandler);
        subscriber.TryGetEventHandler<CommandFailedEvent>(out var failHandler);

        var startEvt = CreateCommandStartedEvent("delete", 789);
        startHandler!(startEvt);

        var failEvt = CreateCommandFailedEvent(789, TimeSpan.FromMilliseconds(10), new Exception("db error"));
        failHandler!(failEvt);

        var activities = GetActivitiesField(subscriber);
        Assert.False(activities.ContainsKey(0));
    }

    [Fact]
    public void Handle_CommandSucceeded_ShouldNotThrow_WhenNoActivityTracked()
    {
        var activitySource = new ActivitySource("test-no-tracked");
        var subscriber = new MongoEventSubscriber(activitySource);
        subscriber.TryGetEventHandler<CommandSucceededEvent>(out var handler);

        var evt = CreateCommandSucceededEvent(9999, TimeSpan.FromMilliseconds(1));
        handler!(evt); // Should not throw
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<int, Activity> GetActivitiesField(MongoEventSubscriber subscriber)
    {
        var field = typeof(MongoEventSubscriber).GetField("_activities", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (System.Collections.Concurrent.ConcurrentDictionary<int, Activity>)field.GetValue(subscriber)!;
    }

    private static CommandStartedEvent CreateCommandStartedEvent(string commandName, int requestId)
    {
        return (CommandStartedEvent)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(CommandStartedEvent));
    }

    private static CommandSucceededEvent CreateCommandSucceededEvent(int requestId, TimeSpan duration)
    {
        return (CommandSucceededEvent)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(CommandSucceededEvent));
    }

    private static CommandFailedEvent CreateCommandFailedEvent(int requestId, TimeSpan duration, Exception ex)
    {
        return (CommandFailedEvent)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(CommandFailedEvent));
    }
}
