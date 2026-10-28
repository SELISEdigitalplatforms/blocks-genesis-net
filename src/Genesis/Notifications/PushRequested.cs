namespace Blocks.Genesis.Notifications;

/// <summary>
/// Message on queue <see cref="NotificationQueues.Push"/>: send a mobile push for one notification. The consumer
/// (logic worker) dedups by <see cref="Id"/>, loads the tenant's <c>firebase-config</c> and the configuration's
/// push template, and pushes to the devices of <see cref="UserIds"/>.
/// </summary>
public sealed record PushRequested
{
    /// <summary>The notification id (<see cref="NotificationRequest.Id"/>).</summary>
    public Guid Id { get; init; }

    /// <summary>The project tenant.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Notification configuration name.</summary>
    public string ConfigName { get; init; } = string.Empty;

    /// <summary>Every receiver: project users and console users.</summary>
    public IReadOnlyList<string> UserIds { get; init; } = [];

    /// <summary>The receivers that are root console users (their devices are in the root DB).</summary>
    public IReadOnlyList<string> RootUserIds { get; init; } = [];

    /// <summary>The event payload as JSON (fills the <c>{{field}}</c> placeholders). Null when none.</summary>
    public string? Payload { get; init; }

    /// <summary>When the notification was published (UTC).</summary>
    public DateTime SentAt { get; init; }
}

/// <summary>Queue names of the notification package.</summary>
public static class NotificationQueues
{
    /// <summary>Mobile push requests, consumed by the logic worker. The consumer declares it.</summary>
    public const string Push = "blocks_notification_push_listener";
}
