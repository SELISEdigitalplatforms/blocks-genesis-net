using Blocks.Genesis.Entities.Os;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.Notifications;

/// <summary>Finds who a send reaches.</summary>
internal interface INotificationReceiverResolver
{
    Task<NotificationReceivers> ResolveAsync(QueuedNotification item, NotificationConfiguration configuration, CancellationToken cancellationToken);
}

/// <summary>
/// Spec steps 3–5. Project users: active users of the tenant, by level. AllUsers and Selected rules count only users
/// inside the send's organization (none without one); users the sending code named count in any organization of the
/// tenant. Console users: named root users with access to the project. Scope picks one side or both. Audience filters
/// only narrow.
/// </summary>
internal sealed class NotificationReceiverResolver : INotificationReceiverResolver
{
    private readonly INotificationDirectory _directory;
    private readonly ILogger<NotificationReceiverResolver> _logger;

    public NotificationReceiverResolver(INotificationDirectory directory, ILogger<NotificationReceiverResolver> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public async Task<NotificationReceivers> ResolveAsync(QueuedNotification item, NotificationConfiguration configuration, CancellationToken cancellationToken)
    {
        // In the root tenant the console users are its own users: read them as project users.
        var scope = _directory.IsRootTenant(item.TenantId) ? NotificationScope.App : configuration.Scope;

        var project = scope == NotificationScope.Console
            ? NotificationReceivers.None
            : await ResolveProjectAsync(item, configuration, cancellationToken);

        IReadOnlyList<string> console = [];
        if (scope != NotificationScope.App)
        {
            var notProjectUsers = ConsoleCandidates(item, configuration).Except(project.ProjectUserIds, StringComparer.Ordinal).ToList();
            console = [.. await _directory.GetConsoleMembersAsync(item.TenantId, notProjectUsers, cancellationToken)];
        }

        return project with { ConsoleUserIds = console };
    }

    /// <summary>Named users, plus the "Done by" user when a Selected audience asks for them.</summary>
    private static IEnumerable<string> ConsoleCandidates(QueuedNotification item, NotificationConfiguration configuration)
    {
        var audience = configuration.EffectiveAudience();
        var doneBy = audience.Level == NotificationAudienceLevel.Selected && audience.AlsoDoneBy ? item.DoneByUserId() : null;
        return doneBy is null ? item.NamedUserIds : item.NamedUserIds.Append(doneBy).Distinct(StringComparer.Ordinal);
    }

    private async Task<NotificationReceivers> ResolveProjectAsync(QueuedNotification item, NotificationConfiguration configuration, CancellationToken cancellationToken)
    {
        var audience = configuration.EffectiveAudience();
        if (audience.Level == NotificationAudienceLevel.AllUsers && item.OrganizationId is null)
        {
            _logger.LogInformation("Notification {Name}: {Level} needs an organization; no project users", item.Name, audience.Level);
            return NotificationReceivers.None;
        }

        if (audience.Level == NotificationAudienceLevel.AllUsers && audience.Filters.Count == 0)
        {
            var members = configuration.EffectiveChannels().Push
                ? await ActiveIdsAsync(item.TenantId, item.OrganizationId, null, cancellationToken)
                : [];
            return new NotificationReceivers(members, item.OrganizationId, []);
        }

        var userIds = audience.Level switch
        {
            NotificationAudienceLevel.AllUsers => await ActiveIdsAsync(item.TenantId, item.OrganizationId, null, cancellationToken),
            NotificationAudienceLevel.Selected => await SelectedIdsAsync(item, audience, cancellationToken),
            _ => await NamedIdsAsync(item, cancellationToken),
        };

        if (audience.Filters.Count > 0 && userIds.Count > 0)
        {
            var subscribed = await _directory.GetSubscribedUserIdsAsync(item.TenantId, audience.Filters, cancellationToken);
            userIds = userIds.Where(subscribed.Contains).ToList();
        }

        return new NotificationReceivers(userIds, null, []);
    }

    private async Task<List<string>> SelectedIdsAsync(QueuedNotification item, NotificationAudience audience, CancellationToken cancellationToken)
    {
        var selected = new List<string>();
        if (audience.HasRules())
        {
            selected.AddRange(await RuleIdsAsync(item, audience, cancellationToken));
        }

        if (audience.AlsoNamedUsers)
        {
            selected.AddRange(await NamedIdsAsync(item, cancellationToken));
        }

        if (audience.AlsoDoneBy && item.DoneByUserId() is { } doneBy)
        {
            selected.AddRange(await ActiveIdsAsync(item.TenantId, null, [doneBy], cancellationToken));
        }

        return selected.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Users the config's UserIds/Roles/Permissions pick: only inside the send's organization, none without one.</summary>
    private async Task<IEnumerable<string>> RuleIdsAsync(QueuedNotification item, NotificationAudience audience, CancellationToken cancellationToken)
    {
        if (item.OrganizationId is not { } organizationId)
        {
            _logger.LogInformation("Notification {Name}: Selected rules need an organization; only named users", item.Name);
            return [];
        }

        var permissions = AudienceRules.ToSet(audience.Permissions);
        var granting = permissions.Count == 0
            ? new HashSet<string>()
            : await _directory.GetRolesGrantingAsync(item.TenantId, organizationId, permissions, cancellationToken);
        var rules = new AudienceRules(audience, organizationId, granting);
        var candidates = await _directory.GetActiveUsersAsync(item.TenantId, organizationId, rules.CandidateIds(), cancellationToken);
        return candidates.Where(rules.Includes).Select(u => u.UserId);
    }

    /// <summary>
    /// Users the sending code named: active users of the tenant in any organization, or with none (e.g. removed from
    /// the send's organization). Never another tenant: only the tenant's own DB is read.
    /// </summary>
    private async Task<List<string>> NamedIdsAsync(QueuedNotification item, CancellationToken cancellationToken) =>
        item.NamedUserIds.Count == 0
            ? []
            : await ActiveIdsAsync(item.TenantId, null, item.NamedUserIds, cancellationToken);

    /// <summary>Active users of the tenant; <paramref name="organizationId"/> null: any organization.</summary>
    private async Task<List<string>> ActiveIdsAsync(string tenantId, string? organizationId, IReadOnlyCollection<string>? userIds,
        CancellationToken cancellationToken)
    {
        var users = await _directory.GetActiveUsersAsync(tenantId, organizationId, userIds, cancellationToken);
        return users.Select(u => u.UserId).Distinct(StringComparer.Ordinal).ToList();
    }
}
