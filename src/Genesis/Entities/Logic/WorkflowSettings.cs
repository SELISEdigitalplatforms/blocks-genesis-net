// New 2026-10-28 (blocks-genesis-net#175): the typed WorkflowEntity.Settings, which was Dictionary<string, string>.
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

// Every property is nullable and null means "use the default". Add new settings as new
// nullable properties; never rename or retype one. Every class has [BsonIgnoreExtraElements].
// Enums are stored as numbers.
[BsonIgnoreExtraElements]
public class WorkflowSettings
{
    public WorkflowAlert? Alert { get; set; }
}

[BsonIgnoreExtraElements]
public class WorkflowAlert
{
    // Off keeps Mode and the target, so switching back on needs no re-entry.
    public bool IsEnabled { get; set; } = true;

    public WorkflowAlertMode Mode { get; set; } = WorkflowAlertMode.Workflow;

    // Used when Mode is Workflow: a published workflow in the same tenant that contains an Error Trigger.
    public string? WorkflowId { get; set; }
}

// Stored as a number. A later mode (e.g. Webhook = 2) is added as a new value plus a new nullable property.
public enum WorkflowAlertMode
{
    Workflow = 1,
}
