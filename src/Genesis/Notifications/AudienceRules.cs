using Blocks.Genesis.Entities.Os;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// The "Selected" rules for one organization. Each non-empty list (UserIds, Roles, Permissions) is one test;
/// Match Any needs one to pass, Match All needs all. Roles and permissions count only inside the send's organization.
/// A permission is held when it is in the user's own list there, or a permission row of that organization grants it
/// to one of the user's roles there. No role inheritance. No list set: nobody.
/// </summary>
internal sealed class AudienceRules
{
    private readonly bool _matchAll;
    private readonly string _organizationId;
    private readonly HashSet<string> _userIds;
    private readonly HashSet<string> _roles;
    private readonly HashSet<string> _permissions;
    private readonly IReadOnlySet<string> _grantingRoles;

    public AudienceRules(NotificationAudience audience, string organizationId, IReadOnlySet<string> grantingRoles)
    {
        _matchAll = audience.Match == NotificationAudienceMatch.All;
        _organizationId = organizationId;
        _userIds = ToSet(audience.UserIds);
        _roles = ToSet(audience.Roles);
        _permissions = ToSet(audience.Permissions);
        _grantingRoles = grantingRoles;
    }

    /// <summary>The only ids that can pass, when the user list alone decides; null when any user of the organization can.</summary>
    public IReadOnlyCollection<string>? CandidateIds()
    {
        var onlyUserRule = _roles.Count == 0 && _permissions.Count == 0;
        return _userIds.Count > 0 && (_matchAll || onlyUserRule) ? _userIds : null;
    }

    public bool Includes(DirectoryUser user)
    {
        var results = Results(user);
        return _matchAll ? results.All(passed => passed) : results.Any(passed => passed);
    }

    /// <summary>One result per set list, computed lazily so Any and All stop early. Empty (so never included) when no list is set.</summary>
    private IEnumerable<bool> Results(DirectoryUser user)
    {
        var any = false;
        if (_userIds.Count > 0)
        {
            any = true;
            yield return _userIds.Contains(user.UserId);
        }

        if (_roles.Count > 0)
        {
            any = true;
            yield return InOrganization(user.Roles).Any(_roles.Contains);
        }

        if (_permissions.Count > 0)
        {
            any = true;
            yield return InOrganization(user.Permissions).Any(_permissions.Contains)
                || InOrganization(user.Roles).Any(_grantingRoles.Contains);
        }

        if (!any)
        {
            yield return false;
        }
    }

    private List<string> InOrganization(IReadOnlyDictionary<string, List<string>> byOrganization) =>
        byOrganization.TryGetValue(_organizationId, out var values) && values is not null ? values : [];

    internal static HashSet<string> ToSet(IEnumerable<string>? values) =>
        values is null ? [] : values.Where(v => !string.IsNullOrWhiteSpace(v)).ToHashSet(StringComparer.Ordinal);
}
