// Moved from blocks-data server/Storage.DomainService/Shared/Entities/File.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Data;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class File : BaseEntity
{
    public string Url { get; set; }
    public string TenantId {  get; set; }

    [BsonRepresentation(BsonType.String)]
    public AccessModifier AccessModifier { get; set; }

    /// <summary>Default access when unshared. Null preserves pre-existing (allow-all) behaviour.</summary>
    [BsonRepresentation(BsonType.String)]
    public ObjectAccessLevel? ObjectAccessLevel { get; set; }
    public Dictionary<string, MetaValue>? MetaData { get; set; } = new Dictionary<string, MetaValue>();
    public string Name { get; set; }
    public string? DirectoryId { get; set; }
    public string SystemName { get; set; }
    public StructureType Type { get; set; }
    public string TypeString { get; set; }
    public long CurrentVersion { get; set; }
    public Dictionary<string, string> AdditionalProperties { get; set; } = new Dictionary<string, string>();

    /// <summary>Cached directory ancestry, ordered root first, ending at the parent directory.</summary>
    public List<string> AncestorIds { get; set; } = new();

    /// <summary>When true the effective access policy is resolved by walking <see cref="AncestorIds"/>.</summary>
    public bool InheritsParentAccess { get; set; } = true;
    public string? Extension { get; set; }
    public long SizeInBytes { get; set; }
    public string? ContentType { get; set; }

    /// <summary>Soft delete. Archived files stay queryable so they can be listed in trash and restored.</summary>
    public bool IsArchived { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ConfigurationName { get; set; }
}
