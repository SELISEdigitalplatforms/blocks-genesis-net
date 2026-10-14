// Moved from blocks-logic server/Notification.DomainService/Shared/Entities/NotificationSubscription.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class NotificationSubscription : SubscriptionFilter
{
    public string Id { get; set; }
    public string ConnectionId { get; set; }
    public string? UserId { get; set; }
    public DateTime CreatedTime { get; set; }
}
