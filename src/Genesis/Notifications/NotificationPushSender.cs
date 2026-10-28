using Blocks.Genesis.Entities.Os;

namespace Blocks.Genesis.Notifications;

/// <summary>Hands mobile push to the logic worker: one <see cref="PushRequested"/> on <see cref="NotificationQueues.Push"/>.</summary>
internal sealed class NotificationPushSender : INotificationChannelSender
{
    private readonly IMessageClient _messageClient;

    public NotificationPushSender(IMessageClient messageClient)
    {
        _messageClient = messageClient;
    }

    public string Channel => "push";

    public bool IsOn(NotificationChannels channels) => channels.Push;

    public Task SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var (item, _, receivers) = delivery;
        var userIds = receivers.ProjectUserIds.Concat(receivers.ConsoleUserIds).Distinct(StringComparer.Ordinal).ToList();
        if (userIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        var message = new PushRequested
        {
            Id = item.Id,
            TenantId = item.TenantId,
            ConfigName = item.Name,
            UserIds = userIds,
            RootUserIds = receivers.ConsoleUserIds,
            Payload = item.PayloadJson,
            SentAt = item.QueuedAt,
        };

        return _messageClient.SendToConsumerAsync(new ConsumerMessage<PushRequested>
        {
            ConsumerName = NotificationQueues.Push,
            Payload = message,
        });
    }
}
