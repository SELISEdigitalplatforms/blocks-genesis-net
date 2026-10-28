using Blocks.Genesis.Entities.Os;

namespace Blocks.Genesis.Notifications;

/// <summary>One resolved send, handed to each channel.</summary>
internal sealed record NotificationDelivery(QueuedNotification Item, NotificationConfiguration Configuration, NotificationReceivers Receivers);

/// <summary>One delivery channel: inbox, live or push. Add a channel by adding an implementation.</summary>
internal interface INotificationChannelSender
{
    /// <summary>Channel name for logs.</summary>
    string Channel { get; }

    bool IsOn(NotificationChannels channels);

    Task SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken);
}
