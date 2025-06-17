using MongoDB.Driver.Core.Events;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoEventSubscriber : IEventSubscriber
    {
        private readonly ActivitySource _activitySource;
        private readonly ReflectionEventSubscriber _subscriber;
        private readonly ConcurrentDictionary<int, Activity> _activities = new ConcurrentDictionary<int, Activity>();

        public MongoEventSubscriber(ActivitySource activitySource)
        {
            _activitySource = activitySource;
            _subscriber = new ReflectionEventSubscriber(this);
        }

        public bool TryGetEventHandler<TEvent>(out Action<TEvent> handler)
        {
            return _subscriber.TryGetEventHandler(out handler);
        }

        // Event for when a command starts
        public void Handle(CommandStartedEvent commandStartedEvent)
        {
            var activity = _activitySource.StartActivity(
                $"MongoDb::{commandStartedEvent.CommandName}",
                ActivityKind.Producer,
                Activity.Current?.Context ?? default
            );

            if (activity != null)
            {
                activity.SetTag("operationName", commandStartedEvent.CommandName);
                activity.SetTag("operationTime", commandStartedEvent.Timestamp.ToString());
                activity.SetTag("requestId", commandStartedEvent.RequestId.ToString());
                _activities[commandStartedEvent.RequestId] = activity;
            }
        }

        // Event for when a command succeeds
        public void Handle(CommandSucceededEvent commandSucceededEvent)
        {
            if (_activities.TryRemove(commandSucceededEvent.RequestId, out var activity))
            {
                activity.SetTag("Status", "Success");
                activity.SetTag("Duration", commandSucceededEvent.Duration.TotalMilliseconds);
                activity.Stop();
            }
        }

        // Event for when a command fails
        public void Handle(CommandFailedEvent commandFailedEvent)
        {
            if (_activities.TryRemove(commandFailedEvent.RequestId, out var activity))
            {
                activity.SetTag("Status", "Failure");
                activity.SetTag("Duration", commandFailedEvent.Duration.TotalMilliseconds);
                activity.SetTag("ExceptionMessage", commandFailedEvent.Failure?.Message ?? "Unknown error");
                activity.Stop();
            }
        }
    }
}
