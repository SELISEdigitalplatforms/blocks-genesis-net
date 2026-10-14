// Moved from blocks-logic server/Workflow.DomainService/Entities/WorkflowItemExecutionEntity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class WorkflowItemExecutionEntity
{
    public required string Id { get; set; }
    public required string WorkflowExecutionId { get; set; }
    public required string TenantId { get; set; }
    public required string NodeId { get; set; }
    public required string NodeExecutionId { get; set; }
    public required string NodeName { get; set; }
    public List<string> ParentItemIds { get; set; } = new();
    public Dictionary<string, string> AncestorMap { get; set; } = new();
    public required string Branch { get; set; }
    public required NodeOutputItemData Data { get; set; }
    public int ItemIndex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
