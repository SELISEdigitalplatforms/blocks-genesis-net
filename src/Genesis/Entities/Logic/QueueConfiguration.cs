// Moved from blocks-logic server/Scheduler.DomainService/Models/QueueConfiguration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

[BsonIgnoreExtraElements]
public class QueueConfiguration
{
    public string QueueName { get; set; } = string.Empty;
}
