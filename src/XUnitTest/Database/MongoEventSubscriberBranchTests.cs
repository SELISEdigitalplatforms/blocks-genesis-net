using Blocks.Genesis;
using MongoDB.Driver.Core.Events;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Database;

public class MongoEventSubscriberBranchTests
{
    [Fact]
    public void Handle_ShouldTrackAndCompleteActivities_WhenListenerIsRegistered()
    {
        var sourceName = $"mongo-events-{Guid.NewGuid():N}";
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var parent = new Activity("mongo-parent").Start();
        using var source = new ActivitySource(sourceName);
        var subscriber = new MongoEventSubscriber(source);

        subscriber.TryGetEventHandler<CommandStartedEvent>(out var startHandler);
        subscriber.TryGetEventHandler<CommandSucceededEvent>(out var successHandler);
        subscriber.TryGetEventHandler<CommandFailedEvent>(out var failHandler);

        // Success path: activity is created (listener present), tracked, then completed.
        startHandler(CreateStarted(requestId: 11));
        successHandler(CreateSucceeded(requestId: 11));

        // Failure path with a null Failure exercises the "Unknown error" fallback.
        startHandler(CreateStarted(requestId: 22));
        failHandler(CreateFailed(requestId: 22));

        Assert.Empty(GetTrackedActivities(subscriber));
    }

    [Fact]
    public void Handle_ShouldIgnoreCompletionEvents_ForUnknownRequests()
    {
        var sourceName = $"mongo-events-{Guid.NewGuid():N}";
        using var source = new ActivitySource(sourceName);
        var subscriber = new MongoEventSubscriber(source);

        subscriber.TryGetEventHandler<CommandSucceededEvent>(out var successHandler);
        subscriber.TryGetEventHandler<CommandFailedEvent>(out var failHandler);

        var success = Record.Exception(() => successHandler(CreateSucceeded(requestId: 404)));
        var failure = Record.Exception(() => failHandler(CreateFailed(requestId: 404)));

        Assert.Null(success);
        Assert.Null(failure);
    }

    private static CommandStartedEvent CreateStarted(int requestId)
        => WithRequestId<CommandStartedEvent>(requestId);

    private static CommandSucceededEvent CreateSucceeded(int requestId)
        => WithRequestId<CommandSucceededEvent>(requestId);

    private static CommandFailedEvent CreateFailed(int requestId)
        => WithRequestId<CommandFailedEvent>(requestId);

    private static TEvent WithRequestId<TEvent>(int requestId)
    {
        object boxed = RuntimeHelpers.GetUninitializedObject(typeof(TEvent));
        var field = typeof(TEvent).GetField("_requestId", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(boxed, requestId);
        return (TEvent)boxed;
    }

    private static System.Collections.ICollection GetTrackedActivities(MongoEventSubscriber subscriber)
    {
        var field = typeof(MongoEventSubscriber).GetField("_activities", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (System.Collections.ICollection)field!.GetValue(subscriber)!;
    }
}
