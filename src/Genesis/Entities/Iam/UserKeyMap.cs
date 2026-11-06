// Moved from blocks-iam server/Iam.DomainService/Shared/Entities/UserKeyMap.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public class UserKeyMap
{
    [BsonId]
    public string ItemId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public DateTime ExpireDate { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string MailPurpose { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool Activated { get; set; }
}
