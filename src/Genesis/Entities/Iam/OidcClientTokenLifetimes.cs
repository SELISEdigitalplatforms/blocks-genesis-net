using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

/// <summary>
/// Optional token lifetimes of one OIDC client, in minutes, stored on its <see cref="OidcClientRegistration"/>.
/// A null field means "use the project value" from <see cref="IdentityConfiguration"/>.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class OidcClientTokenLifetimes
{
    public const int MinMinutes = 1;
    public const int MaxAccessTokenMinutes = 1440;
    public const int MaxRefreshTokenMinutes = 525600;

    public int? AccessTokenValidForNumberMinutes { get; set; }

    /// <summary>Sliding (idle) refresh-token lifetime.</summary>
    public int? RefreshTokenValidForNumberMinutes { get; set; }

    /// <summary>Hard cap of a refresh-token lineage, fixed at first issue.</summary>
    public int? AbsoluteRefreshTokenValidForNumberMinutes { get; set; }

    public bool HasAnyValue() =>
        AccessTokenValidForNumberMinutes.HasValue
        || RefreshTokenValidForNumberMinutes.HasValue
        || AbsoluteRefreshTokenValidForNumberMinutes.HasValue;

    public static bool IsValidAccessMinutes(int value) => value is >= MinMinutes and <= MaxAccessTokenMinutes;

    public static bool IsValidRefreshMinutes(int value) => value is >= MinMinutes and <= MaxRefreshTokenMinutes;
}
