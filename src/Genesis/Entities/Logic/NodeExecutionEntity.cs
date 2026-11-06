// Moved from blocks-logic server/Workflow.DomainService/Entities/NodeExecutionEntity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

/// <summary>
/// Metadata-only model for tracking a single node's execution within a workflow.
/// Does NOT store input/output payloads - those live in WorkflowItemExecutionModel.
/// Contains only execution metadata: timing, status, counts, and preview information.
/// </summary>
[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class NodeExecutionEntity
{

    public required string Id { get; set; }
    public required string NodeId { get; set; }
    public required string NodeName { get; set; }
    public required string NodeType { get; set; }
    public required string NodeVersion { get; set; }
    public int RunIndex { get; set; }
    public NodeExecutionStatus Status { get; set; } = NodeExecutionStatus.Pending;

    public int InputItemCount { get; set; }
    public int OutputItemCount { get; set; }
    public Dictionary<string, int> OutputCountsByBranch { get; set; } = new();

    // Timing
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    // Error / retry
    public string? Error { get; set; }
    public int AttemptNumber { get; set; } = 1;
}
