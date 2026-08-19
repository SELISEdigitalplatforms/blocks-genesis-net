using System.Text.Json.Serialization;

namespace Blocks.Genesis;

/// <summary>
/// The authoritative identity behind a delegation grant.
/// <para>
/// This record — not the message <c>SecurityContext</c> — is what IAM trusts when minting a
/// delegated access token. Property names are the wire contract and are serialized in
/// PascalCase so blocks-genesis-py and blocks-iam read the same JSON.
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
}
