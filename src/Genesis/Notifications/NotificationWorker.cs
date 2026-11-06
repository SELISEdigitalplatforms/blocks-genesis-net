using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.Notifications;

/// <summary>The single reader of the notification buffer. One failed item is logged and the next one runs.</summary>
internal sealed class NotificationWorker : BackgroundService
{
    private readonly NotificationQueue _queue;
    private readonly INotificationDispatcher _dispatcher;
    private readonly ILogger<NotificationWorker> _logger;

    public NotificationWorker(NotificationQueue queue, INotificationDispatcher dispatcher, ILogger<NotificationWorker> logger)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave host start-up at once; the loop runs on the thread pool.
        await Task.Yield();
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DispatchOneAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping: a normal end. Items still buffered are lost (no outbox).
        }
    }

    internal async Task DispatchOneAsync(QueuedNotification item, CancellationToken stoppingToken)
    {
        try
        {
            await _dispatcher.DispatchAsync(item, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogError("Notification {Name} failed: {Error}", item.Name, ex.GetType().Name); // NOSONAR S6667 — type only
        }
    }
}
