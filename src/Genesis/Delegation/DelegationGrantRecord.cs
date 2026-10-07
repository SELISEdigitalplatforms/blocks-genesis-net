using System.Text.Json.Serialization;

namespace Blocks.Genesis;

/// <summary>
/// The authoritative identity behind a delegation grant.
/// <para>
/// This record — not the message <c>SecurityContext</c> — is what IAM trusts when minting a
/// delegated access token. Property names are the wire contract and are serialized in
/// PascalCase so blocks-genesis-py and blocks-iam read the same JSON.
/// </para>
/// <para>
/// A grant names exactly one subject: a user (<see cref="UserId"/> plus <see cref="TokenVersion"/>
/// and <see cref="SecurityStamp"/>), or an OAuth client authenticated with
/// <c>client_credentials</c> (<see cref="ClientId"/>, no version material).
/// </para>
/// <para>
/// A grant made while impersonating also names the session it belongs to
/// (<see cref="ImpersonationSessionId"/>) and the tenant the user really lives in
/// (<see cref="OriginalTenantId"/>). The second is a <em>pointer</em>, not a claim of authority:
/// it says which directory holds the session record, and that record must then agree about the
/// user, the target tenant and its own root tenant before anything is minted.
/// </para>
/// </summary>
public sealed record DelegationGrantRecord
{
    [JsonPropertyName("TenantId")]
    public string TenantId { get; init; } = string.Empty;

    [JsonPropertyName("UserId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("OrganizationId")]
    public string OrganizationId { get; init; } = string.Empty;

    [JsonPropertyName("TokenVersion")]
    public string TokenVersion { get; init; } = string.Empty;

    [JsonPropertyName("SecurityStamp")]
    public string SecurityStamp { get; init; } = string.Empty;

    /// <summary>The client credential behind a machine-to-machine grant. Empty on a user grant.</summary>
    [JsonPropertyName("ClientId")]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// The impersonation session this grant was made under, or empty when the caller was acting as
    /// themselves.
    /// <para>
    /// Present, it makes the grant a continuation of a live impersonation rather than a second,
    /// independent grant of authority: IAM resolves the user against the session's root tenant,
    /// refuses the grant once the session stops, and mints a token that still says it is
    /// impersonated. Absent — every grant written before this field existed — the redemption is
    /// exactly what it was.
    /// </para>
    /// </summary>
    [JsonPropertyName("ImpersonationSessionId")]
    public string ImpersonationSessionId { get; init; } = string.Empty;

    /// <summary>
    /// The tenant the impersonating user actually belongs to, or empty when not impersonating.
    /// <para>
    /// Needed because the impersonation session is written in that tenant's own database, and
    /// <see cref="TenantId"/> here is the tenant being worked <em>in</em> — so without this there
    /// is nowhere to look the session up. It is only ever a lookup hint: every value that decides
    /// the outcome is read back from the session record, which must name this same tenant as its
    /// root and <see cref="TenantId"/> as its target.
    /// </para>
    /// </summary>
    [JsonPropertyName("OriginalTenantId")]
    public string OriginalTenantId { get; init; } = string.Empty;

    /// <summary>True when the grant was made while impersonating.</summary>
    [JsonIgnore]
    public bool IsImpersonated => !string.IsNullOrWhiteSpace(ImpersonationSessionId);

    /// <summary>True when the grant names a client and no user.</summary>
    [JsonIgnore]
    public bool IsClientGrant => string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(ClientId);
}
