using Blocks.Genesis.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace XUnitTest.Notifications;

public sealed class NotificationWorkerTests
{
    private sealed class FlakyDispatcher : INotificationDispatcher
    {
        public List<string> Done { get; } = [];
        public TaskCompletionSource AllDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DispatchAsync(QueuedNotification item, CancellationToken cancellationToken)
        {
            if (item.Name == "bad")
            {
                throw new InvalidOperationException("database down");
            }

            Done.Add(item.Name);
            if (Done.Count == 2)
            {
                AllDone.SetResult();
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FailedItem_IsLogged_AndTheNextOneStillRuns()
    {
        var queue = new NotificationQueue(NullLogger<NotificationQueue>.Instance);
        var dispatcher = new FlakyDispatcher();
        using var worker = new NotificationWorker(queue, dispatcher, NullLogger<NotificationWorker>.Instance);
        foreach (var name in new[] { "first", "bad", "after" })
        {
            queue.Enqueue(Sample.Item() with { Name = name });
        }

        await worker.StartAsync(CancellationToken.None);
        await dispatcher.AllDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(["first", "after"], dispatcher.Done);
    }
}
