// Moved from blocks-iam server/Authentication.DomainService/Shared/Entities/BlocksClientConfig.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;

namespace Blocks.Genesis.Entities.Iam;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class BlocksClientConfig : BaseEntity
{
    public string? ClientName { get; set; }
}
