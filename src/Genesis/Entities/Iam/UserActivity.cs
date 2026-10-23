// Moved from blocks-iam server/Iam.DomainService/Shared/Entities/UserActivity.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public class UserActivity : BaseEntity
{
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public required UserActivityCategory Category { get; set; }
    public required string Event { get; set; }
    public required string UserId { get; set; }
    public required string ActorUserId { get; set; }
    public string? TenantId { get; set; }
    public string? Outcome { get; set; }
    public string? ReasonCode { get; set; }
    public string? Severity { get; set; }
    public string? Source { get; set; }
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
    public string? SessionId { get; set; }
    public string? ClientId { get; set; }
    public ActivityContext? Context { get; set; }
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}
