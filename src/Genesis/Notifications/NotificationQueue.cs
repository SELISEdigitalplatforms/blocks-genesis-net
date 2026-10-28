using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// The in-memory buffer between <see cref="INotificationPublisher"/> and the worker: 1000 items; when full, the new
/// item is dropped and a warning carries the total dropped so far. Lost on process exit (no outbox).
/// </summary>
internal sealed class NotificationQueue
{
    internal const int Capacity = 1000;
    private const int WarnEvery = 100;

    private readonly Channel<QueuedNotification> _channel;
    private readonly ILogger<NotificationQueue> _logger;
    private long _dropped;

    public NotificationQueue(ILogger<NotificationQueue> logger)
    {
        _logger = logger;
        var options = new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<QueuedNotification>(options, OnDropped);
    }

    public ChannelReader<QueuedNotification> Reader => _channel.Reader;

    /// <summary>Total items dropped because the buffer was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Enqueue(QueuedNotification item) => _channel.Writer.TryWrite(item);

    private void OnDropped(QueuedNotification item)
    {
        var dropped = Interlocked.Increment(ref _dropped);
        if (dropped == 1 || dropped % WarnEvery == 0)
        {
            _logger.LogWarning("Notification buffer full: {Name} dropped, {Dropped} dropped in total", item.Name, dropped);
        }
    }
}
