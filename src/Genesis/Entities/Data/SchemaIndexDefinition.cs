// Moved from blocks-data server/DataGateway.DomainService/Entities/SchemaIndexDefinition.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Data;

[BsonIgnoreExtraElements]
public class SchemaIndexDefinition : GraphQlBaseEntity
{
    public string SchemaDefinitionItemId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<IndexFieldSpec> Fields { get; set; } = new List<IndexFieldSpec>();
    public bool IsUnique { get; set; }
}

[BsonIgnoreExtraElements]
public class IndexFieldSpec
{
    public string FieldName { get; set; } = string.Empty;

    /// <summary>
    /// MongoDB sort direction for this key within the index: 1 (ascending) or -1 (descending).
    /// </summary>
    public int Direction { get; set; } = 1;
}
