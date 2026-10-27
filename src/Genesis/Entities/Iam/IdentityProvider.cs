// Moved from blocks-iam server/Authentication.DomainService/Shared/Entities/IdentityProvider.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
[SystemSchemaRules(RuleLevel.Warned)]
public class IdentityProvider : BaseEntity
{
    public required string Provider { get; set; } // e.g., google, microsoft, apple, okta, auth0, blocks-idp
    public required string ProviderType { get; set; } // social, enterprise, custom, internal
    public string? Protocol { get; set; } = "oidc"; // e.g., oidc, oauth2, saml, ldap
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }

    /// <summary>
    /// The Blocks Secrets id of the client secret. When set, the value lives in the secret store and
    /// <see cref="ClientSecret"/> is empty; when null (every provider saved before this field existed),
    /// <see cref="ClientSecret"/> holds the plaintext. Not stored when null.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? ClientSecretReference { get; set; }

    public string? Issuer { get; set; }
    public  string? AuthorizationUrl { get; set; }
    public  string? TokenUrl { get; set; }
    public string? UserInfoUrl { get; set; }
    public string? JwksUri { get; set; }
    public string? WellKnownUrl { get; set; }
    public List<string> RedirectUris { get; set; } = [];
    public string? Scope { get; set; }
    public string? ResponseType { get; set; }
    public List<string> GrantTypes { get; set; } = [];
    public bool RequirePkce { get; set; }
    public required string TokenEndpointAuthMethod { get; set; }
    public List<string> InitialRoles { get; set; } = [];
    public List<string> InitialPermissions { get; set; } = [];
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? Icon { get; set; }
    
    // Apple-specific properties
    public string? TeamId { get; set; }
    public string? KeyId { get; set; }
    public string? PrivateKey { get; set; }

    /// <summary>
    /// The Blocks Secrets id of <see cref="PrivateKey"/>, with the same rule as <see cref="ClientSecretReference"/>:
    /// set means the key is in the secret store, null means <see cref="PrivateKey"/> holds it. Not stored when null.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? PrivateKeyReference { get; set; }

    public string? AppleAudience { get; set; }
}
