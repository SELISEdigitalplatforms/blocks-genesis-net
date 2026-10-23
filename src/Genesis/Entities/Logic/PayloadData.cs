// Moved from blocks-logic server/Notification.DomainService/Shared/Entities/PayloadData.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class PayloadData
{
    public string? UserId { get; set; }
    public List<SubscriptionFilter> SubscriptionFilters { get; set; }
    public string NotificationType { get; set; }
    public string ResponseKey { get; set; }
    public string ResponseValue { get; set; }
}
