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

    /// <summary>True when the grant names a client and no user.</summary>
    [JsonIgnore]
    public bool IsClientGrant => string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(ClientId);
}
