// Moved from blocks-iam server/Authentication.DomainService/Oidc/Contracts/OidcContracts.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public sealed class RefreshTokenModel
{
    [BsonId]
    public string TokenId { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string? OrganizationId { get; set; }
    public string? ClientId { get; set; }
    public string? Audience { get; set; }
    public string? Scope { get; set; }
    public string? SessionId { get; set; }

    /// <summary>Signup-link restricted session marker preserved across refresh.</summary>
    public bool IsLinkAuthentication { get; set; }
    public List<string> RestrictedRoles { get; set; } = [];
    public List<string> RestrictedPermissions { get; set; } = [];
    public List<string> Amr { get; set; } = [];

    /// <summary>
    /// Identifies one refresh-token lineage: the sequence of tokens created by a single
    /// authentication and linked by rotation. Set to the token's own <see cref="TokenId"/> when a new
    /// lineage is created, and copied unchanged by every rotation. Null on documents written before
    /// lineage tracking shipped; such a document is read as a lineage of one (see
    /// <see cref="EffectiveRefreshTokenSessionId"/>).
    /// </summary>
    public string? RefreshTokenSessionId { get; set; }

    /// <summary>
    /// The <see cref="TokenId"/> of the successor minted when this token was rotated away. Written in
    /// the same update that sets <c>RevokeReason = "superseded_by_rotation"</c>, and used to replay a
    /// rotation for a request that still carries the predecessor inside the grace window. Null on every
    /// document written before the grace period shipped, and on every revocation for another reason.
    /// </summary>
    public string? SupersededByTokenId { get; set; }

    public string? GrantType { get; set; }
    public DeviceInformation? DeviceInformation { get; set; }
    public DateTime IssuedUtc { get; set; }
    public DateTime SlidingExpiry { get; set; }
    public DateTime AbsoluteExpiry { get; set; }
    public bool IsRevoked { get; set; }
    public string? RevokeReason { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool Impersonated { get; set; }
    public string? ImpersonationId { get; set; }

    /// <summary>
    /// The lineage this token belongs to, treating a pre-lineage document as a lineage of one.
    /// </summary>
    public string EffectiveRefreshTokenSessionId =>
        string.IsNullOrWhiteSpace(RefreshTokenSessionId) ? TokenId : RefreshTokenSessionId!;

    public bool IsExpired()
    {
        var now = DateTime.UtcNow;
        return now >= SlidingExpiry || now >= AbsoluteExpiry;
    }

    /// <summary>
    /// A token is active only while the server would actually accept it: unrevoked and past neither
    /// clock. This deliberately matches <see cref="IsExpired"/> so listings never advertise a session
    /// the refresh endpoint would reject.
    /// </summary>
    public bool IsActive(DateTime now) => !IsRevoked && now < SlidingExpiry && now < AbsoluteExpiry;
}

[BsonIgnoreExtraElements]
public sealed class IdpSessionAccount
{
    public string UserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTime LoginAt { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class IdpSessionModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = Guid.NewGuid().ToString("n");
    public string TenantId { get; set; } = string.Empty;
    public List<IdpSessionAccount> Accounts { get; set; } = [];
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public DateTime IdleExpiry { get; set; }
    public DateTime AbsoluteExpiry { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsExpired()
    {
        var now = DateTime.UtcNow;
        return now >= IdleExpiry || now >= AbsoluteExpiry;
    }
}
