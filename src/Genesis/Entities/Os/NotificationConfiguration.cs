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

    /// <summary>Who it is for: <see cref="NotificationScope.App"/> consumer users, <see cref="NotificationScope.Console"/> root
    /// console users (rows stay in the root tenant only), or <see cref="NotificationScope.Both"/>.</summary>
    [BsonIgnoreIfDefault]
    public NotificationScope Scope { get; set; }

    /// <summary>Who receives it in this tenant, set by the project admin. Null: <see cref="DefaultAudience"/> applies.</summary>
    [BsonIgnoreIfNull]
    public NotificationAudience? Audience { get; set; }

    /// <summary>Who receives it when the tenant set nothing. Set by the root tenant on built-ins and copied with them.
    /// Null: affected users only.</summary>
    [BsonIgnoreIfNull]
    public NotificationAudience? DefaultAudience { get; set; }

    /// <summary>Built-in personal notice: a tenant may not change who receives it (only turn it on or off).</summary>
    [BsonIgnoreIfDefault]
    public bool AudienceLocked { get; set; }

    /// <summary>Most sends per minute per tenant. 0: the notifier's default (120).</summary>
    [BsonIgnoreIfDefault]
    public int RateLimitPerMinute { get; set; }

    /// <summary>In-app (live) channel. On by default: left out of the stored document while true, and a document
    /// without the field reads as true, so records written before the field existed keep sending live.</summary>
    [BsonDefaultValue(true)]
    [BsonIgnoreIfDefault]
    public bool LiveEnabled { get; set; } = true;

    /// <summary>Mobile push channel. Needs <see cref="PushTemplate"/>.</summary>
    [BsonIgnoreIfDefault]
    public bool PushEnabled { get; set; }

    /// <summary>Localization keys and link of the mobile push message.</summary>
    [BsonIgnoreIfNull]
    public NotificationPushTemplate? PushTemplate { get; set; }

    /// <summary>The audience that applies: the tenant's own unless locked, else the default, else affected users only.</summary>
    public NotificationAudience EffectiveAudience() =>
        (AudienceLocked ? null : Audience) ?? DefaultAudience ?? new NotificationAudience();

    /// <summary>
    /// The channels that apply. A record saved before the channel fields existed with <c>ChannelToNotify</c>
    /// Firebase (and no <see cref="PushEnabled"/>) reads as push only; SignalR reads as live. Inbox is
    /// <see cref="EnablePersistence"/>.
    /// </summary>
    public NotificationChannels EffectiveChannels()
    {
        if (ChannelToNotify == NotifierTypes.Firebase && !PushEnabled)
        {
            return new NotificationChannels(Live: false, Push: true, Inbox: EnablePersistence);
        }

        return new NotificationChannels(Live: LiveEnabled, Push: PushEnabled, Inbox: EnablePersistence);
    }

    /// <summary>
    /// False for a built-in job result or progress stream: a page waits for it, so switching it off would leave
    /// that page waiting. Custom configs and built-in alerts can always be disabled.
    /// </summary>
    public bool CanBeDisabled() => !IsBuiltIn || (Kind != NotificationKind.Job && Kind != NotificationKind.Progress);
}

/// <summary>Who a notification is for, and so where a built-in is kept.</summary>
public enum NotificationScope
{
    /// <summary>Consumer users of the project. Copied to every project tenant.</summary>
    App = 0,

    /// <summary>Root console users (also while they impersonate a project). Kept in the root tenant only.</summary>
    Console = 1,

    /// <summary>Both consumer users and root console users. Copied to every project tenant.</summary>
    Both = 2,
}

/// <summary>Which users of the send's organization receive a notification.</summary>
public enum NotificationAudienceLevel
{
    /// <summary>"Affected users only": the users the sending code names (record owner, affected user).</summary>
    AffectedOnly = 0,

    /// <summary>"All users": every active user of the send's organization.</summary>
    AllUsers = 1,

    /// <summary>"Selected": the users, roles and permissions in the rule lists, joined by <see cref="NotificationAudience.Match"/>.</summary>
    Selected = 2,
}

/// <summary>The channels a notification goes out on (see <see cref="NotificationConfiguration.EffectiveChannels"/>).</summary>
public readonly record struct NotificationChannels(bool Live, bool Push, bool Inbox);

/// <summary>
/// The mobile push message. Title and body are Localization keys (module <c>notifications</c>); their text, per language,
/// may hold <c>{{field}}</c> placeholders filled from the payload. No free text is stored here.
/// </summary>
[BsonIgnoreExtraElements]
public class NotificationPushTemplate
{
    public string TitleKey { get; set; } = string.Empty;
    public string BodyKey { get; set; } = string.Empty;

    /// <summary>Link the app opens; may hold placeholders.</summary>
    [BsonIgnoreIfNull]
    public string? Link { get; set; }
}

/// <summary>How the rule lists of a custom audience combine.</summary>
public enum NotificationAudienceMatch
{
    /// <summary>A user in any list receives it.</summary>
    Any = 0,

    /// <summary>A user must be in every list that is set.</summary>
    All = 1,
}

/// <summary>
/// Who receives a notification. Always inside the send's organization (org must): every level picks only active
/// users of that organization. Live filters only narrow the result, they never add anyone.
/// </summary>
[BsonIgnoreExtraElements]
public class NotificationAudience
{
    [BsonIgnoreIfDefault]
    public NotificationAudienceLevel Level { get; set; }

    [BsonIgnoreIfDefault]
    public NotificationAudienceMatch Match { get; set; }

    public List<string> UserIds { get; set; } = [];
    public List<string> Roles { get; set; } = [];

    /// <summary>Permission resources, for example <c>blocks-iam::iam::users</c>.</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Keep only users with a live subscription that matches one of these. Never adds a user.</summary>
    public List<Logic.SubscriptionFilter> Filters { get; set; } = [];

    /// <summary>Selected level: also send to the users the sending code names (for example the locked user).</summary>
    [BsonIgnoreIfDefault]
    public bool AlsoNamedUsers { get; set; }

    /// <summary>True when the Selected rule lists name at least one user, role or permission.</summary>
    public bool HasRules() => UserIds.Count > 0 || Roles.Count > 0 || Permissions.Count > 0;
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
