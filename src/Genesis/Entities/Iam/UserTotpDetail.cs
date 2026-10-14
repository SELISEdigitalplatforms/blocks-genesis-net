// Moved from blocks-iam server/Mfa.DomainService/TOTP/UserTotpDetail.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public class UserTotpDetail : BaseEntity
{
    public string? ImageUri { get; set; }
    public string? TowFactorId { get; set; }
    public string? Secret { get; set; }
}
