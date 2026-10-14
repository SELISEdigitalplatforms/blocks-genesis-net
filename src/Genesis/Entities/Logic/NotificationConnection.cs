// Moved from blocks-logic server/Notification.DomainService/Notification/NotificationConnection.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

[BsonIgnoreExtraElements]
public class NotificationConnection
{
    [BsonId]
    public string Id { get; set; }
    public string ConnectionId { get; set; }
    public string? UserId { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.Now;
}
