// Moved from blocks-logic server/Workflow.DomainService/Entities/WorkflowVersionEntity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

[BsonIgnoreExtraElements]
public class WorkflowVersionEntity : BaseEntity
{
    public required string WorkflowId { get; set; }
    public required string TenantId { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public required WorkflowEntity Snapshot { get; set; }
}
