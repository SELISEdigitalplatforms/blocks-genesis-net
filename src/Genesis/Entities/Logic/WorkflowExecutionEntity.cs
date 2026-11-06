// Moved from blocks-logic server/Workflow.DomainService/Entities/WorkflowExecutionEntity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Logic;

[BsonIgnoreExtraElements]
public class WorkflowExecutionEntity
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string WorkflowId { get; set; }
    public required string WorkflowName { get; set; }
    public required WorkflowEntity WorkflowSnapshot { get; set; }
    public WorkflowExecutionStatus Status { get; set; } = WorkflowExecutionStatus.Init;
    public WorkflowExecutionMode ExecutionMode { get; set; } = WorkflowExecutionMode.Test;
    public List<string> ActiveNodeIds { get; set; } = new();
    public BsonDocument Context { get; set; } = new BsonDocument();
    public List<NodeExecutionEntity> NodeExecutions { get; set; } = new();
    public required TriggerMetadata TriggerMetadata { get; set; } = new();
    // Retry / hierarchy
    public string? OriginalExecutionId { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>W3C trace id (32 lowercase hex) generated when the execution is created. Every execution stage
    /// log line is stamped with it so the run's logs can be fetched by this id. Null on executions created
    /// before execution logging existed.</summary>
    public string? TraceId { get; set; }
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class TriggerMetadata
{
    public string? TriggerNodeId { get; set; }
    public string? TriggerType { get; set; }
    public BsonArray? TriggerData { get; set; }
}
