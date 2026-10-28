using Blocks.Genesis.Entities.Os;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.Notifications;

/// <summary>Sends one queued notification: config, rate limit, receivers, then each channel that is on.</summary>
internal interface INotificationDispatcher
{
    Task DispatchAsync(QueuedNotification item, CancellationToken cancellationToken);
}

/// <summary>
/// Runs one item in its own context (target tenant and sender), so key-value reads and message headers use the right
/// tenant. A failing channel is logged and the others still run. Logs carry the configuration name only.
/// </summary>
internal sealed class NotificationDispatcher : INotificationDispatcher
{
    internal const int DefaultRateLimitPerMinute = 120;

    private readonly INotificationConfigReader _configReader;
    private readonly INotificationRateLimiter _rateLimiter;
    private readonly INotificationReceiverResolver _resolver;
    private readonly IEnumerable<INotificationChannelSender> _channels;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(INotificationConfigReader configReader, INotificationRateLimiter rateLimiter,
        INotificationReceiverResolver resolver, IEnumerable<INotificationChannelSender> channels, ILogger<NotificationDispatcher> logger)
    {
        _configReader = configReader;
        _rateLimiter = rateLimiter;
        _resolver = resolver;
        _channels = channels;
        _logger = logger;
    }

    public async Task DispatchAsync(QueuedNotification item, CancellationToken cancellationToken)
    {
        BlocksContext.SetContext(item.ToContext());
        try
        {
            await SendAsync(item, cancellationToken);
        }
        finally
        {
            BlocksContext.SetContext(null);
        }
    }

    private async Task SendAsync(QueuedNotification item, CancellationToken cancellationToken)
    {
        var configuration = await _configReader.GetAsync(item.TenantId, item.Name, cancellationToken);
        if (configuration is null)
        {
            _logger.LogInformation("Notification {Name}: no configuration; nothing sent", item.Name);
            return;
        }

        if (configuration.IsDisabled && configuration.CanBeDisabled())
        {
            _logger.LogInformation("Notification {Name}: disabled; nothing sent", item.Name);
            return;
        }

        var channels = configuration.EffectiveChannels();
        var active = _channels.Where(channel => channel.IsOn(channels)).ToList();
        if (active.Count == 0)
        {
            _logger.LogInformation("Notification {Name}: no channel on; nothing sent", item.Name);
            return;
        }

        var limit = configuration.RateLimitPerMinute > 0 ? configuration.RateLimitPerMinute : DefaultRateLimitPerMinute;
        if (!await _rateLimiter.TryAcquireAsync(item.TenantId, item.Name, limit))
        {
            return;
        }

        var receivers = await _resolver.ResolveAsync(item, configuration, cancellationToken);
        if (receivers.IsEmpty)
        {
            _logger.LogInformation("Notification {Name}: no receivers; nothing sent", item.Name);
            return;
        }

        var delivery = new NotificationDelivery(item, configuration, receivers);
        foreach (var channel in active)
        {
            await SendToChannelAsync(channel, delivery, cancellationToken);
        }
    }

    private async Task SendToChannelAsync(INotificationChannelSender channel, NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendAsync(delivery, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: driver and broker messages can hold ids or values.
            _logger.LogError("Notification {Name}: {Channel} failed: {Error}", delivery.Item.Name, channel.Channel, ex.GetType().Name); // NOSONAR S6667 — type only
        }
    }
}
