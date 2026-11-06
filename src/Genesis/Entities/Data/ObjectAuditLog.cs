// Moved from blocks-data server/Storage.DomainService/Shared/Entities/ObjectAuditLog.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Data;

/// <summary>
/// One recorded access decision or access-management action against a directory or file.
/// Denied attempts are recorded as well as granted ones, so the log answers who tried
/// as well as who succeeded.
/// </summary>
[BsonIgnoreExtraElements]
public class ObjectAuditLog : BaseEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string ResourceId { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public ObjectResourceType ResourceType { get; set; }

    public string UserId { get; set; } = string.Empty;

    /// <summary>One of View, Download, Edit, Delete, Manage, Grant, Revoke or Share.</summary>
    public string Action { get; set; } = string.Empty;

    public bool Granted { get; set; }

    /// <summary>Free-form context such as a version number or the principal that was granted.</summary>
    public string? Detail { get; set; }

    /// <summary>The deleted record (blocks-data <c>data-delete</c> rows only). Not stored when null.</summary>
    [BsonIgnoreIfNull]
    public BsonDocument? Record { get; set; }
}
