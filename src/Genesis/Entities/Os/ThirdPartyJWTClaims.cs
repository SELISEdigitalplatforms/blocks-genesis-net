// Moved from blocks-os server/Identifier.DomainService/Shared/Entities/ThirdPartyJWTClaims.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Os;

[BsonIgnoreExtraElements]
public class ThirdPartyJWTClaims : BaseEntity
{
    public string UserId { get; set; }
    public string Email { get; set; }
    public string Name { get; set; }
    public string UserName { get; set; }
    public string Roles { get; set; }
}
