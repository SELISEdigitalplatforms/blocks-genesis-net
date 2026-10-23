// Moved from blocks-logic server/Workflow.DomainService/Nodes/NodeOutputItem.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class NodeOutputItemData
{
    public BsonValue Parameters { get; set; } = new BsonDocument();
    public BsonValue Input { get; set; } = new BsonDocument();
    public BsonValue Output { get; set; } = new BsonDocument();

}
