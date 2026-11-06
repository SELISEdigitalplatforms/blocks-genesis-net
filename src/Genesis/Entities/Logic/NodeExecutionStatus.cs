// Moved from blocks-logic server/Workflow.DomainService/Enums/NodeExecutionStatus.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

public enum NodeExecutionStatus
{
    Pending = 2,
    Running = 3,
    Completed = 4,
    Failed = 5,
}
