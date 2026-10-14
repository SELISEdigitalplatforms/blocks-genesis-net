// Moved from blocks-logic server/Notification.DomainService/Shared/Entities/OfflineNotification.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class OfflineNotification
{
    [BsonId]
    public string Id { get; set; }
    public string CorrelationId { get; set; }
    public PayloadData Payload { get; set; }

    [BsonIgnoreIfNull]
    public dynamic DenormalizedPayload { get; set; }
    public DateTime CreatedTime { get; set; }

    [BsonIgnoreIfNull]
    public List<string> ReadByUserIds { get; set; }
    public List<string> ReadByRoles { get; set; }

    [BsonIgnore]
    public bool IsRead { get; set; }
}
