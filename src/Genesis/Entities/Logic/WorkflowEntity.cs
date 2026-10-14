// Moved from blocks-logic server/Workflow.DomainService/Entities/WorkflowEntity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

[BsonIgnoreExtraElements]
public class WorkflowEntity : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public required string TenantId { get; set; }

    public List<NodeEntity> Nodes { get; set; } = new();

    public List<EdgeEnity> Edges { get; set; } = new();

    public Dictionary<string, string> Settings { get; set; } = new();

    public bool IsDirty { get; set; } = true;

    public bool IsPublished { get; set; } = false;

    public string? PublishedVersionId { get; set; } = null;

    public string? LastPublishedVersionId { get; set; } = null;

    public PublishedWorkflowMeta? PublishedMeta { get; set; } = null;
    public TestWorkflowMeta? TestMeta { get; set; } = null;

    public string Description { get; set; } = string.Empty;

}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class PublishedWorkflowMeta
{
    public List<NodeEntity> TriggerNodes { get; set; } = new();
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class TestWorkflowMeta
{
    public List<NodeEntity> ListenerTriggerNodes { get; set; } = new();
    public List<string> UserIds { get; set; } = new();
    public bool IsListening { get; set; } = false;
    public string? CompletionNodeId { get; set; } = null;
}
