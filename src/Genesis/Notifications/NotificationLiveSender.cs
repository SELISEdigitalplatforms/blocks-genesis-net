using Blocks.Genesis.Entities.Os;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// Live send through <see cref="IHubContext{THub}"/> over the Redis backplane, to groups only: each user's
/// <c>tenant:T:user:id</c>, or <c>tenant:T:org:O</c> once for "All users". Skipped when the item waited over 15 minutes.
/// </summary>
internal sealed class NotificationLiveSender : INotificationChannelSender
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);
    private const int GroupsPerSend = 500;

    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationLiveSender> _logger;

    public NotificationLiveSender(IHubContext<NotificationHub> hubContext, TimeProvider timeProvider, ILogger<NotificationLiveSender> logger)
    {
        _hubContext = hubContext;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Channel => "live";

    public bool IsOn(NotificationChannels channels) => channels.Live;

    public async Task SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var (item, configuration, receivers) = delivery;
        if (_timeProvider.GetUtcNow().UtcDateTime - item.QueuedAt > MaxAge)
        {
            _logger.LogInformation("Notification {Name}: live skipped, older than {Minutes} minutes", item.Name, MaxAge.TotalMinutes);
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.NotifyMethod))
        {
            _logger.LogWarning("Notification {Name}: live skipped, the configuration has no NotifyMethod", item.Name);
            return;
        }

        var message = Message(item);
        foreach (var groups in Groups(item.TenantId, receivers).Chunk(GroupsPerSend))
        {
            await _hubContext.Clients.Groups(groups).SendAsync(configuration.NotifyMethod, message, cancellationToken);
        }
    }

    private static IEnumerable<string> Groups(string tenantId, NotificationReceivers receivers)
    {
        var projectGroups = receivers.OrganizationWideId is { } organizationId
            ? [NotificationGroups.ForTenantOrg(tenantId, organizationId)]
            : receivers.ProjectUserIds.Select(id => NotificationGroups.ForTenantUser(tenantId, id));
        return projectGroups
            .Concat(receivers.ConsoleUserIds.Select(id => NotificationGroups.ForTenantUser(tenantId, id)))
            .Distinct(StringComparer.Ordinal);
    }

    private static NotificationLiveMessage Message(QueuedNotification item) => new()
    {
        NotificationId = item.Id,
        ConfigurationName = item.Name,
        ResponseKey = item.ResponseKey,
        ResponseValue = item.ResponseValue,
        SubscriptionFilters = item.SubscriptionFilters,
        DenormalizedPayload = item.PayloadJson,
        OrganizationIds = item.OrganizationId is null ? null : [item.OrganizationId],
    };
}
