// Moved from blocks-logic server/Notification.DomainService/Shared/Entities/SubscriptionFilter.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class SubscriptionFilter
{
    public string Context { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
