using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis;

/// <summary>
/// One external identity provider a tenant trusts tokens from, stored in the
/// <c>JwtThirdPartyProviders</c> collection in the root database.
/// </summary>
/// <remarks>
/// <para>
/// A tenant may hold several, including two of the same kind sharing an issuer — two applications
/// inside one Auth0 tenant, say. Selection narrows on <c>iss</c> + <c>aud</c> first and falls back
/// to the <c>x-blocks-idp</c> header only when those cannot tell two candidates apart.
/// </para>
/// <para>
/// Providers that share an issuer should differ by audience. Where two share both, nothing
/// cryptographic distinguishes their tokens and the header alone selects which
/// <see cref="ClaimsMapping"/> applies — so those two must carry equivalent privilege.
/// </para>
/// </remarks>
[BsonIgnoreExtraElements]
public class ThirdPartyJwtProvider : BaseEntity
{
    /// <summary>Tenant this provider belongs to. Every lookup is scoped by it.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Stable identifier the <c>x-blocks-idp</c> header names. Unique within a tenant.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display name — Auth0, Okta, Keycloak. Carries no behaviour.</summary>
    public string ProviderName { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    /// <summary>
    /// Matched against the token's <c>iss</c> exactly and ordinally. Auth0's carries a trailing
    /// slash and Okta's does not; normalising would invite matching the wrong provider.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Accepted audiences. <b>Empty disables audience validation entirely</b>, which also collapses
    /// every provider sharing this issuer into one candidate set.
    /// </summary>
    public List<string> Audiences { get; set; } = [];

    /// <summary>
    /// Accepted signing algorithms, pinned into <c>ValidAlgorithms</c>. Also selects the key
    /// source: symmetric members read <see cref="SigningSecretCipher"/>, the rest read
    /// <see cref="JwksUrl"/>.
    /// </summary>
    public List<JwtSigningAlgorithm> Algorithms { get; set; } = [];

    /// <summary>Key source for the asymmetric families. Public, fetched over HTTPS.</summary>
    public string JwksUrl { get; set; } = string.Empty;

    /// <summary>
    /// Key source for the HMAC family: the shared secret, AES-GCM encrypted under a key derived
    /// from the tenant's salt. Never the plaintext, and never returned by a read API.
    /// </summary>
    public string SigningSecretCipher { get; set; } = string.Empty;

    /// <summary>Cookie this provider's token may arrive in, for the non-header path.</summary>
    public string CookieKey { get; set; } = string.Empty;

    /// <summary>Organization scope a provider falls back to when none is configured.</summary>
    /// <remarks>Read as tenant-wide by consumers, so it is the widest scope, not a narrow one.</remarks>
    public const string DefaultOrganization = "default";

    private string _defaultOrganizationId = DefaultOrganization;

    /// <summary>
    /// Organization every caller arriving through this provider acts in. Never blank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately distinct from <see cref="BaseEntity.OrganizationId"/>, which is row metadata
    /// saying where this configuration document lives. This one is configuration: it names the
    /// organization scope granted to the tokens this provider validates.
    /// </para>
    /// <para>
    /// <b>Blank is normalised away on assignment</b>, so nothing that reads this has to guard
    /// against it. A document written before the field existed carries no element and keeps the
    /// initial value; a document written by a form that left the field empty carries <c>""</c>,
    /// which would otherwise overwrite that initial value during deserialisation. Both land on
    /// <see cref="DefaultOrganization"/> here, once, rather than at each place that reads an
    /// organization -- some of which collapse a blank to <c>"default"</c> while others deny it
    /// outright, so a blank would leave the scope a caller receives depending on which layer read
    /// it.
    /// </para>
    /// <para>
    /// A provider-level value, so every caller through one provider shares it. Per-user
    /// organization selection needs a provisioned Blocks user to hold memberships against; until
    /// that exists this is the single answer, and afterwards it becomes the default that a
    /// resolved membership overrides.
    /// </para>
    /// <para>
    /// <b><c>"default"</c> is not a narrow scope.</b> Consumers read it as tenant-wide, so a
    /// provider left on the initial value grants the widest organization scope there is. Narrow it
    /// deliberately for any provider that should not have that.
    /// </para>
    /// </remarks>
    public string DefaultOrganizationId
    {
        get => _defaultOrganizationId;
        set => _defaultOrganizationId =
            string.IsNullOrWhiteSpace(value) ? DefaultOrganization : value.Trim();
    }

    /// <summary>
    /// How this provider's claims map onto a Blocks context. Held on the provider so configuration
    /// and mapping are saved together.
    /// </summary>
    public ThirdPartyClaimsMapping ClaimsMapping { get; set; } = new();
}

/// <summary>
/// Which claim supplies each field of the Blocks context.
/// </summary>
/// <remarks>
/// Each value is a <b>literal claim name</b> first. Claim names are opaque and routinely contain
/// dots — every namespaced OIDC claim is a URI, such as
/// <c>https://myapp.example.com/user_id</c> — so a value is only split on <c>.</c> when no claim
/// by that exact name exists. The split form addresses a property inside a claim whose value is a
/// JSON object, as in Keycloak's <c>realm_access.roles</c>.
/// </remarks>
[BsonIgnoreExtraElements]
public class ThirdPartyClaimsMapping
{
    /// <summary>
    /// Claim supplying the subject. <c>"sub"</c> resolves through the standard subject claim.
    /// The resolved value is suffixed with <c>_external</c> to form the Blocks user id.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary><c>"email"</c> resolves through the standard email claim.</summary>
    public string UserName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Claim supplying roles. A JSON array claim arrives already flattened into one claim per
    /// element, so a namespaced roles claim needs no JSON parsing.
    /// </summary>
    public string Roles { get; set; } = string.Empty;

    /// <summary>True when any field is mapped. An entirely blank mapping cannot identify anyone.</summary>
    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(UserId)
        || !string.IsNullOrWhiteSpace(Email)
        || !string.IsNullOrWhiteSpace(UserName)
        || !string.IsNullOrWhiteSpace(Name)
        || !string.IsNullOrWhiteSpace(Roles);
}
