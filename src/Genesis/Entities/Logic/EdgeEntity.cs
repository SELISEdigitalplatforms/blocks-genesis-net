// Moved from blocks-logic server/Workflow.DomainService/Entities/EdgeEntity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class EdgeEnity
{
    public required string Id { get; set; }
    public required string Source { get; set; }
    public required string Target { get; set; }
    public required string SourceHandle { get; set; }
    public required string TargetHandle { get; set; }
}
