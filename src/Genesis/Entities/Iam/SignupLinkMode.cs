// Moved from blocks-iam server/Iam.DomainService/SignupLinks/SignupLinkMode.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Iam;

/// <summary>
/// How a redeemed link hands the invitee a session.
/// <para>
/// <see cref="Oidc"/> ends in an authorize URL the browser follows. <see cref="Embedded"/>
/// returns tokens directly, for a tenant running with OIDC off whose construct hosts its own
/// join screen.
/// </para>
/// <para>
/// Zero is <see cref="Oidc"/> deliberately: both entities are <c>BsonIgnoreExtraElements</c>,
/// so a document written before this field existed deserialises to Oidc and stays correct
/// with no migration and no backfill. Never infer the mode from an empty ClientId -- that
/// would turn a validation bug into a silent mode switch.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignupLinkMode
{
    Oidc = 0,
    Embedded = 1
}
