// Moved from blocks-release server/Devops.DomainService/Deployment/Entities/HostingProvider.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Release;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class HostingProvider
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public List<Region>? Region { get; set; }

}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class Region
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public List<MachineConfig>? MachineSpecs { get; set; }
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class MachineConfig
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string Ram { get; set; }
    public string CPU { get; set; }
    public string Bandwidth { get; set; }
    public string Status { get; set; }
}
