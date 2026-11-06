// Moved from blocks-iam server/Iam.DomainService/SignupLinks/SignupLinkStatus.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Iam;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignupLinkStatus
{
    Active = 0,
    Redeemed = 1,
    Exhausted = 2,
    Revoked = 3,
    Expired = 4
}
