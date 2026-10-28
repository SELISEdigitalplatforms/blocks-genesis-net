using Blocks.Genesis.Entities.Logic;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// A request as it waits in the buffer: everything taken from the caller's <see cref="BlocksContext"/> at publish
/// time, because the worker runs without it.
/// </summary>
internal sealed record QueuedNotification
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;

    /// <summary>The send's organization (<see cref="SendOrganization"/>); null when none.</summary>
    public string? OrganizationId { get; init; }

    public IReadOnlyList<string> NamedUserIds { get; init; } = [];
    public string? PayloadJson { get; init; }
    public List<SubscriptionFilter>? SubscriptionFilters { get; init; }
    public string? ResponseKey { get; init; }
    public string? ResponseValue { get; init; }
    public DateTime QueuedAt { get; init; }
    public NotificationSender Sender { get; init; } = NotificationSender.None;

    /// <summary>The context the worker runs this item in: the target tenant and the sender.</summary>
    public BlocksContext ToContext() => BlocksContext.Create(
        tenantId: TenantId,
        roles: [],
        userId: Sender.UserId,
        isAuthenticated: false,
        requestUri: string.Empty,
        organizationId: OrganizationId,
        expireOn: DateTime.MinValue,
        email: string.Empty,
        permissions: [],
        userName: string.Empty,
        phoneNumber: string.Empty,
        displayName: string.Empty,
        oauthToken: string.Empty,
        originalTenantId: string.IsNullOrWhiteSpace(Sender.OriginalTenantId) ? TenantId : Sender.OriginalTenantId,
        impersonated: Sender.Impersonated);
}

/// <summary>Who sent it, from the caller's context. No token, roles or personal data.</summary>
internal sealed record NotificationSender(string UserId, string OriginalTenantId, bool Impersonated)
{
    public static NotificationSender None { get; } = new(string.Empty, string.Empty, false);
}
