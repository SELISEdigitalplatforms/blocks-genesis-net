using Blocks.Genesis.Entities.Logic;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// What a client receives on the configuration's <c>NotifyMethod</c>: the same fields as logic's NotifyRequest
/// before the move, so existing clients keep working. Receiver lists are not sent to clients.
/// </summary>
public sealed record NotificationLiveMessage
{
    public Guid NotificationId { get; init; }
    public string ConfigurationName { get; init; } = string.Empty;
    public string? ResponseKey { get; init; }
    public string? ResponseValue { get; init; }
    public IReadOnlyList<SubscriptionFilter>? SubscriptionFilters { get; init; }

    /// <summary>The payload JSON, as a string (clients parse it as before).</summary>
    public string? DenormalizedPayload { get; init; }

    public bool SaveDenormalizedPayloadAsAnObject { get; init; }
    public bool ContentAvailable { get; init; }
    public string ConnectionId { get; init; } = string.Empty;
    public IReadOnlyList<string>? UserIds { get; init; }
    public IReadOnlyList<string>? Roles { get; init; }
    public IReadOnlyList<string>? OrganizationIds { get; init; }
}
