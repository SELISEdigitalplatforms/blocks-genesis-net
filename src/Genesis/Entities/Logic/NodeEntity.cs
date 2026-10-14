// Moved from blocks-logic server/Workflow.DomainService/Entities/NodeEntity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class NodeEntity
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public required string Type { get; set; }
    public required string Version { get; set; }
    public required Position Position { get; set; }
    public Handle? Handle { get; set; } = new();
    public BsonDocument Parameters { get; set; } = new BsonDocument();
    public BsonDocument Settings { get; set; } = new BsonDocument();
    public BsonArray? PinData { get; set; }
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class Position
{
    public double X { get; set; }
    public double Y { get; set; }
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class Handle
{
    public List<string> Sources = new();
    public List<string> Targets = new();
}
