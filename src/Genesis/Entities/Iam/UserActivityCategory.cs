// Moved from blocks-iam server/Iam.DomainService/Shared/Dtos/UserActivityCategory.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Iam;

/// <summary>
/// Top-level grouping applied to user activity entries so the activity log
/// can be filtered and reported on. Each entry belongs to exactly one
/// category, and the categories map 1:1 to sections of the user profile UI.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserActivityCategory
{
    /// <summary>Account-level changes: profile updates, status changes, lifecycle events.</summary>
    Account,

    /// <summary>Authentication events: logins, logouts, MFA challenges, password changes.</summary>
    Auth,

    /// <summary>Resource access events: API calls, role/permission grants, organisation membership changes.</summary>
    Resource,

    /// <summary>Compliance and audit events: terms acceptance, admin overrides, suspicious activity.</summary>
    Audit
}
