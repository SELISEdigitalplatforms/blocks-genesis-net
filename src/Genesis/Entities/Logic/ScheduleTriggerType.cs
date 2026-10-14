// Moved from blocks-logic server/Scheduler.DomainService/Enums/ScheduleTriggerType.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

public enum ScheduleTriggerType
{
    Webhook = 1,
    Queue = 2
}
