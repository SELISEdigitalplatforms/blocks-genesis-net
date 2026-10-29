// Moved from blocks-iam server/Authentication.DomainService/Shared/Entities/OidcClientRegistration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public class OidcClientRegistration : BaseEntity
{
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The Blocks Secrets id of the client secret. When set, the value lives in the secret store and
    /// <see cref="ClientSecret"/> is empty; when null (every client saved before this field existed),
    /// <see cref="ClientSecret"/> holds the plaintext. Not stored when null.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? ClientSecretReference { get; set; }

    public List<string> RedirectUris { get; set; } = new();
    public List<string> PostLogoutRedirectUris { get; set; } = new();
    public List<string> AllowedScopes { get; set; } = new();
    public List<string> AllowedResponseTypes { get; set; } = new() { "code" };
    public string? ClientName { get; set; }
    public string? TokenEndpointAuthMethod { get; set; }
    public bool RequirePkce { get; set; } = true;
    public bool RequireConsent { get; set; }
    public string? FrontChannelLogoutUri { get; set; }
    public string? BackChannelLogoutUri { get; set; }
    public bool IsAutoRedirect { get; set; }
    public string? ExternalDiscoveryEndpoint { get; set; }
    public bool IsActive { get; set; } = true;
    public string? LoginMode { get; set; }
    public string? ClientType { get; set; }
    public bool UseTokensCookie { get; set; } = true; // Default: tokens in cookies
    public bool RequireMfa { get; set; }
    public List<UserMfaType>? AllowedMfaMethods { get; set; }
    public bool IsDeviceFlowClient { get; set; } = false;


    [BsonIgnore]
    public string? Scope
    {
        get => AllowedScopes.Count == 0 ? null : string.Join(' ', AllowedScopes);
        set => AllowedScopes = string.IsNullOrWhiteSpace(value)
            ? new()
            : value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    [BsonIgnore]
    public string? ClientDisplayName
    {
        get => ClientName;
        set => ClientName = value;
    }

    public bool RegisterAsIdentityProvider { get; set; }

    /// <summary>Optional per-client token lifetimes; null = the project values. Not stored when null.</summary>
    [BsonIgnoreIfNull]
    public OidcClientTokenLifetimes? TokenLifetimes { get; set; }

    /// <summary>How this client narrows the tenant's sign-in policy. Null = Default method and providers. Not stored when null.</summary>
    [BsonIgnoreIfNull]
    public OidcClientLoginPolicy? LoginPolicy { get; set; }

}
