using MongoDB.Driver.Core.Events;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    internal class MongoEventSubscriber : IEventSubscriber
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
            var securityContext = BlocksContext.GetContext();

            var activity = _activitySource.StartActivity(
                $"MongoDb::{commandStartedEvent.CommandName}",
                ActivityKind.Producer,
                Activity.Current?.Context ?? default
            );

            if (activity != null)
            {
                activity.AddTag("dbName", commandStartedEvent.DatabaseNamespace.DatabaseName);
                activity.AddTag("operationName", commandStartedEvent.CommandName);
                activity.AddTag("operationTime", commandStartedEvent.Timestamp.ToString());
                activity.SetCustomProperty("TenantId", securityContext?.TenantId);
                activity.AddTag("requestId", commandStartedEvent.RequestId.ToString());

                // Store the activity with the request ID for later retrieval
                _activities[commandStartedEvent.RequestId] = activity;
            }
        }

        // Event for when a command succeeds
        public void Handle(CommandSucceededEvent commandSucceededEvent)
        {
            if (_activities.TryRemove(commandSucceededEvent.RequestId, out var activity))
            {
                activity.AddTag("Status", "Success");
                activity.AddTag("Duration", commandSucceededEvent.Duration.TotalMilliseconds);
                activity.Stop();
            }
        }

        // Event for when a command fails
        public void Handle(CommandFailedEvent commandFailedEvent)
        {
            if (_activities.TryRemove(commandFailedEvent.RequestId, out var activity))
            {
                activity.AddTag("Status", "Failure");
                activity.AddTag("Duration", commandFailedEvent.Duration.TotalMilliseconds);
                activity.AddTag("ExceptionMessage", commandFailedEvent.Failure?.Message ?? "Unknown error");
                activity.Stop();
            }
        }
    }
}
