// Moved from blocks-logic server/Scheduler.DomainService/Entities/Schedule.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

[BsonIgnoreExtraElements]
public class Schedule : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Payload { get; set; }
    public string CronExpression { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public ScheduleKind Kind { get; set; } = ScheduleKind.Application;
    public ScheduleTriggerType TriggerType { get; set; } = ScheduleTriggerType.Queue;
    public WebhookConfiguration? Webhook { get; set; }
    public QueueConfiguration? Queue { get; set; }
}
