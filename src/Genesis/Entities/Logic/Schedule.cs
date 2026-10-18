// Moved from blocks-logic server/Scheduler.DomainService/Entities/Schedule.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis.Entities.Logic;

[BsonIgnoreExtraElements]
[SystemSchemaRules(RuleLevel.Normal)]
public class Schedule : BaseEntity
{
    [AllowRules(Validation = RuleAccess.Allowed)]
    public string Name { get; set; } = string.Empty;
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? Description { get; set; }
    public string Payload { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string CronExpression { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public DateTime? StartDate { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public DateTime? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public ScheduleKind Kind { get; set; } = ScheduleKind.Application;
    public ScheduleTriggerType TriggerType { get; set; } = ScheduleTriggerType.Queue;
    public WebhookConfiguration? Webhook { get; set; }
    public QueueConfiguration? Queue { get; set; }
}
