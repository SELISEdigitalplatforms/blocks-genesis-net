// Moved from blocks-release server/Devops.DomainService/Deployment/Models/Response/BuildEventResponse.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Collections.Immutable;

namespace Blocks.Genesis.Entities.Release;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class BuildEventResponse
{
    public string? Id { get; set; }
    public string BuildId { get; set; }
    public string EventType { get; set; }
    public string Message { get; set; }
    public string EventGroup { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdateDate { get; set; } = DateTime.UtcNow;

}
