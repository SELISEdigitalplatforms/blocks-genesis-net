using Blocks.Genesis.Entities.Logic;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// One notification to send. Who receives it is decided by the tenant's notification configuration named
/// <see cref="Name"/>, not by the sender: the sender only names the users the event is about.
/// </summary>
public sealed class NotificationRequest
{
    /// <summary>Configuration name, for example <c>iam-user-updated</c> (key <c>notification-config:&lt;Name&gt;</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The users the event is about (record owner, affected user). Used by "Affected users only",
    /// by "Also notify the users named by the sending service", and to find console users.</summary>
    public IReadOnlyList<string> NamedUserIds { get; init; } = [];

    /// <summary>The send's organization, used only when the send has no user token (service, worker). A user-token
    /// send always uses the token's organization.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>Event data: any object (serialized to JSON) or a JSON string. Max 256 KB.</summary>
    public object? Payload { get; init; }

    /// <summary>Carried with the notification so a client page can match it; does not choose receivers.</summary>
    public IReadOnlyList<SubscriptionFilter>? SubscriptionFilters { get; init; }

    public string? ResponseKey { get; init; }

    public string? ResponseValue { get; init; }

    /// <summary>Target tenant. Null: the tenant of the current <see cref="BlocksContext"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>Set by <see cref="INotificationPublisher.Publish"/> (kept when the caller set one). Push dedup key.</summary>
    public Guid Id { get; set; }
}
