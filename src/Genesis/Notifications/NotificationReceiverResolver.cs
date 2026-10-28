using Blocks.Genesis.Entities.Os;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.Notifications;

/// <summary>Finds who a send reaches.</summary>
internal interface INotificationReceiverResolver
{
    Task<NotificationReceivers> ResolveAsync(QueuedNotification item, NotificationConfiguration configuration, CancellationToken cancellationToken);
}

/// <summary>
/// Spec steps 3–5. Project users: active users of the tenant inside the send's organization, by level. Console
/// users: named root users with access to the project. Scope picks one side or both. Audience filters only narrow.
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
            var notProjectUsers = item.NamedUserIds.Except(project.ProjectUserIds, StringComparer.Ordinal).ToList();
            console = [.. await _directory.GetConsoleMembersAsync(item.TenantId, notProjectUsers, cancellationToken)];
        }

        return project with { ConsoleUserIds = console };
    }

    private async Task<NotificationReceivers> ResolveProjectAsync(QueuedNotification item, NotificationConfiguration configuration, CancellationToken cancellationToken)
    {
        var audience = configuration.EffectiveAudience();
        if (audience.Level != NotificationAudienceLevel.AffectedOnly && item.OrganizationId is null)
        {
            _logger.LogInformation("Notification {Name}: {Level} needs an organization; no project users", item.Name, audience.Level);
            return NotificationReceivers.None;
        }

        if (audience.Level == NotificationAudienceLevel.AllUsers && audience.Filters.Count == 0)
        {
            var members = configuration.EffectiveChannels().Push
                ? await ActiveIdsAsync(item, null, cancellationToken)
                : [];
            return new NotificationReceivers(members, item.OrganizationId, []);
        }

        var userIds = audience.Level switch
        {
            NotificationAudienceLevel.AllUsers => await ActiveIdsAsync(item, null, cancellationToken),
            NotificationAudienceLevel.Selected => await SelectedIdsAsync(item, audience, cancellationToken),
            _ => item.NamedUserIds.Count == 0 ? new List<string>() : await ActiveIdsAsync(item, item.NamedUserIds, cancellationToken),
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
        var organizationId = item.OrganizationId!;
        var selected = new List<string>();
        if (audience.HasRules())
        {
            var permissions = AudienceRules.ToSet(audience.Permissions);
            var granting = permissions.Count == 0
                ? new HashSet<string>()
                : await _directory.GetRolesGrantingAsync(item.TenantId, organizationId, permissions, cancellationToken);
            var rules = new AudienceRules(audience, organizationId, granting);
            var candidates = await _directory.GetActiveUsersAsync(item.TenantId, organizationId, rules.CandidateIds(), cancellationToken);
            selected.AddRange(candidates.Where(rules.Includes).Select(u => u.UserId));
        }

        if (audience.AlsoNamedUsers && item.NamedUserIds.Count > 0)
        {
            selected.AddRange(await ActiveIdsAsync(item, item.NamedUserIds, cancellationToken));
        }

        return selected.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Active users of the send's organization (any organization when the send has none).</summary>
    private async Task<List<string>> ActiveIdsAsync(QueuedNotification item, IReadOnlyCollection<string>? userIds, CancellationToken cancellationToken)
    {
        var users = await _directory.GetActiveUsersAsync(item.TenantId, item.OrganizationId, userIds, cancellationToken);
        return users.Select(u => u.UserId).Distinct(StringComparer.Ordinal).ToList();
    }
}
