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

    /// <summary>The project tenant of a console user's row, which is kept in the root DB. Null: the DB's own tenant.</summary>
    [BsonIgnoreIfNull]
    public string? TenantId { get; set; }

    /// <summary>The send's organization. A row without <see cref="PayloadData.UserId"/> is for every user of it.</summary>
    [BsonIgnoreIfNull]
    public string? OrganizationId { get; set; }
}
