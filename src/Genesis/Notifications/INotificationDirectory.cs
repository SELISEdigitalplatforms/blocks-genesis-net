using Blocks.Genesis.Entities.Logic;

namespace Blocks.Genesis.Notifications;

/// <summary>A project user as audience rules see them: roles and own permissions per organization.</summary>
internal sealed record DirectoryUser(
    string UserId,
    IReadOnlyDictionary<string, List<string>> Roles,
    IReadOnlyDictionary<string, List<string>> Permissions);

/// <summary>The user data receiver resolution reads. Every call names the tenant; nothing is read from context.</summary>
internal interface INotificationDirectory
{
    /// <summary>True when <paramref name="tenantId"/> is the root (console) tenant.</summary>
    bool IsRootTenant(string tenantId);

    /// <summary>
    /// Active users of the tenant DB. <paramref name="organizationId"/> null: any organization; else members of it
    /// (for <c>default</c> also users without one). <paramref name="userIds"/> null: no id filter.
    /// </summary>
    Task<IReadOnlyList<DirectoryUser>> GetActiveUsersAsync(string tenantId, string? organizationId,
        IReadOnlyCollection<string>? userIds, CancellationToken cancellationToken);

    /// <summary>Roles that a live (not archived) permission row of the organization grants for one of the resources.</summary>
    Task<IReadOnlySet<string>> GetRolesGrantingAsync(string tenantId, string organizationId,
        IReadOnlyCollection<string> resources, CancellationToken cancellationToken);

    /// <summary>Users with a live hub subscription (last 3 h) matching one of the filters.</summary>
    Task<IReadOnlySet<string>> GetSubscribedUserIdsAsync(string tenantId, IReadOnlyCollection<SubscriptionFilter> filters,
        CancellationToken cancellationToken);

    /// <summary>The ids among <paramref name="userIds"/> that are console (root) users with access to the project.</summary>
    Task<IReadOnlySet<string>> GetConsoleMembersAsync(string tenantId, IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken);
}
