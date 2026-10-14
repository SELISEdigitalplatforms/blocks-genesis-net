// Moved from blocks-data server/Storage.DomainService/Shared/Entities/Structure.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Data;

[BsonIgnoreExtraElements]
public class Structure : BaseEntity
{
    public Structure() => MetaData = new Dictionary<string, MetaValue>();
    public Dictionary<string, MetaValue> MetaData { get; set; }
    public string Name { get; set; }
    public string? ParentId { get; set; }
    public string SystemName { get; set; }
    public StructureType Type { get; set; }
    public string TypeString { get; set; }
    public string[] AllowedFileExtensions { get; set; }
}
