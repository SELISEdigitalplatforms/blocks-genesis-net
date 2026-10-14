// Moved from blocks-data server/DataGateway.DomainService/Entities/SchemaDefinition.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Data;

[BsonIgnoreExtraElements]
public class SchemaDefinition : GraphQlBaseEntity
{
    public string CollectionName { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new List<FieldDefinition>();
    public string SchemaName { get; set; } = string.Empty;
    public SchemaType SchemaType { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string ProjectShortKey { get; set; } = string.Empty;
    public SchemaAccessLevel ReadAccessLevel { get; set; } = SchemaAccessLevel.User;
    public SchemaAccessLevel WriteAccessLevel { get; set; } = SchemaAccessLevel.User;
    public SchemaAccessLevel EditAccessLevel { get; set; } = SchemaAccessLevel.User;
    public SchemaAccessLevel DeleteAccessLevel { get; set; } = SchemaAccessLevel.User;

    public string GetSchemaNameForProject()
    {
        return SchemaName;
    }
}
