// Moved from blocks-data server/Storage.DomainService/Shared/Entities/MetaValue.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Data;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class MetaValue
{
    public string Type { get; set; }
    public string Value { get; set; }
}
