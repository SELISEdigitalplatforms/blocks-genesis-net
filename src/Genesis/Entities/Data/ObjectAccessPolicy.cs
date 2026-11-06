// Moved from blocks-data server/Storage.DomainService/Shared/Entities/ObjectAccessPolicy.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Data;

/// <summary>
/// A single access control entry against one directory or file. Entries are evaluated
/// per resource, walking cached ancestors when the resource inherits.
/// </summary>
/// <remarks>
/// Every enum property stores its string name. The data migration writes string
/// values, so an int representation here would silently fail to match on read.
/// </remarks>
[BsonIgnoreExtraElements]
public class ObjectAccessPolicy : BaseEntity
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>ItemId of the directory or file this entry applies to.</summary>
    public string ResourceId { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public ObjectResourceType ResourceType { get; set; }

    [BsonRepresentation(BsonType.String)]
    public ObjectPrincipalType PrincipalType { get; set; }

    /// <summary>User id, role slug or organization id. Null when the principal is Everyone.</summary>
    public string? PrincipalId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public ObjectPermission Permission { get; set; }

    [BsonRepresentation(BsonType.String)]
    public ObjectEffect Effect { get; set; }

    /// <summary>Higher wins between entries at the same scope.</summary>
    public int Priority { get; set; }

    /// <summary>Optional expiry for a time bound grant. Null means the grant does not expire.</summary>
    public DateTime? ExpiresAt { get; set; }

    public string? GrantedBy { get; set; }
}
