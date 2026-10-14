// Moved from blocks-data server/DataGateway.DomainService/Entities/SchemaChangeLog.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Data;

/// <summary>
/// Represents a schema change log.
/// </summary>
[BsonIgnoreExtraElements]
public class SchemaChangeLog : GraphQlBaseEntity
{
    public string SchemaId { get; set; } = string.Empty;
    public SchemaChangeType ChangeType { get; set; }
    public bool DoesServerAdaptChanges { get; set; } = false;
}
