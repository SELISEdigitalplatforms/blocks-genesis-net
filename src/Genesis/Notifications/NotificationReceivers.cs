namespace Blocks.Genesis.Notifications;

/// <summary>Who a send reaches, after audience, organization and scope.</summary>
/// <param name="ProjectUserIds">Project users of the tenant. For an organization-wide send it is filled only when
/// push needs the user ids; inbox and live use <paramref name="OrganizationWideId"/> instead.</param>
/// <param name="OrganizationWideId">Set for "All users" without filters: one inbox row and one live send for the
/// whole organization.</param>
/// <param name="ConsoleUserIds">Root console users with access to the project.</param>
internal sealed record NotificationReceivers(
    IReadOnlyList<string> ProjectUserIds,
    string? OrganizationWideId,
    IReadOnlyList<string> ConsoleUserIds)
{
    public static NotificationReceivers None { get; } = new([], null, []);

    public bool IsEmpty => OrganizationWideId is null && ProjectUserIds.Count == 0 && ConsoleUserIds.Count == 0;
}
