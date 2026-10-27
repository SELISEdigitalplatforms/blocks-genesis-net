// Moved from blocks-os server/Configuration.DomainService/Notification/Entities/NotificationConfiguration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
// 2026-10-21: optional built-in fields. Each is left out of the stored document while it has its default value,
// so records written before this change, and custom configs, keep exactly the same bytes.
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Os;

[BsonIgnoreExtraElements]
public class NotificationConfiguration : BaseEntity
{
    public string Name { get; set; }
    public NotifierTypes ChannelToNotify { get; set; }
    public NotificationReceiverTypes NotificationType { get; set; }
    public string NotifyMethod { get; set; }
    public bool EnablePersistence { get; set; }

    /// <summary>Platform-defined config. Only the root tenant creates, changes or removes it; it is copied to every tenant.</summary>
    [BsonIgnoreIfDefault]
    public bool IsBuiltIn { get; set; }

    /// <summary>Set by a tenant to stop this notification. Built-in <see cref="NotificationKind.Job"/> and
    /// <see cref="NotificationKind.Progress"/> configs cannot be disabled (see <see cref="CanBeDisabled"/>).</summary>
    [BsonIgnoreIfDefault]
    public bool IsDisabled { get; set; }

    /// <summary>What the notification tells, in one or two sentences.</summary>
    [BsonIgnoreIfNull]
    public string? Description { get; set; }

    [BsonIgnoreIfDefault]
    public NotificationImportance Importance { get; set; }

    [BsonIgnoreIfDefault]
    public NotificationKind Kind { get; set; }

    /// <summary>The Blocks service that sends it, for example <c>blocks-data</c>.</summary>
    [BsonIgnoreIfNull]
    public string? SourceService { get; set; }

    /// <summary>How an app built on Blocks should use it.</summary>
    [BsonIgnoreIfNull]
    public string? ConsumerAppRecommendation { get; set; }

    /// <summary>What applies when an app uses Blocks without the Blocks portal.</summary>
    [BsonIgnoreIfNull]
    public string? ConsumerAppNote { get; set; }

    /// <summary>Where a built-in is needed. <see cref="NotificationScope.Console"/> rows stay in the root tenant only.</summary>
    [BsonIgnoreIfDefault]
    public NotificationScope Scope { get; set; }

    /// <summary>Who may receive it. Null or no rules: the sender decides, as before. A sender can only narrow this list.</summary>
    [BsonIgnoreIfNull]
    public NotificationAudience? Audience { get; set; }

    /// <summary>
    /// False for a built-in job result or progress stream: a page waits for it, so switching it off would leave
    /// that page waiting. Custom configs and built-in alerts can always be disabled.
    /// </summary>
    public bool CanBeDisabled() => !IsBuiltIn || (Kind != NotificationKind.Job && Kind != NotificationKind.Progress);
}

/// <summary>Where a built-in notification is needed.</summary>
public enum NotificationScope
{
    /// <summary>Useful to apps: copied to every project tenant.</summary>
    App = 0,

    /// <summary>Only the Blocks console uses it: kept in the root tenant, not copied to projects.</summary>
    Console = 1,
}

/// <summary>How the rule lists of an <see cref="NotificationAudience"/> combine.</summary>
public enum NotificationAudienceMatch
{
    /// <summary>A user in any list receives it.</summary>
    Any = 0,

    /// <summary>A user must be in every list that is set.</summary>
    All = 1,
}

/// <summary>
/// Who may receive a notification. <see cref="EveryoneInTenant"/> means every active user of the tenant; otherwise
/// each non-empty list gives a set of users and <see cref="Match"/> joins them. Roles and Permissions are checked
/// inside <see cref="OrganizationIds"/> when that list is set.
/// </summary>
[BsonIgnoreExtraElements]
public class NotificationAudience
{
    [BsonIgnoreIfDefault]
    public bool EveryoneInTenant { get; set; }

    [BsonIgnoreIfDefault]
    public NotificationAudienceMatch Match { get; set; }

    public List<string> UserIds { get; set; } = [];
    public List<string> OrganizationIds { get; set; } = [];
    public List<string> Roles { get; set; } = [];
    public List<string> Permissions { get; set; } = [];

    /// <summary>Users with a live subscription that matches one of these (same match as filter notifications).</summary>
    public List<Logic.SubscriptionFilter> Filters { get; set; } = [];

    /// <summary>True when at least one rule list is set.</summary>
    public bool HasRules() =>
        UserIds.Count > 0 || OrganizationIds.Count > 0 || Roles.Count > 0 || Permissions.Count > 0 || Filters.Count > 0;

    /// <summary>True when this audience limits who receives the notification.</summary>
    public bool IsSet() => EveryoneInTenant || HasRules();
}

/// <summary>How much a notification matters to the person who receives it.</summary>
public enum NotificationImportance
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>What a notification is for.</summary>
public enum NotificationKind
{
    None = 0,

    /// <summary>Result of a background job a user waits for.</summary>
    Job = 1,

    /// <summary>Live progress stream of a running operation.</summary>
    Progress = 2,

    /// <summary>A failure nobody would otherwise see.</summary>
    Alert = 3,

    /// <summary>A change to an account, credential or secret.</summary>
    Security = 4,

    /// <summary>Someone changed what a user can do.</summary>
    Access = 5,
}
