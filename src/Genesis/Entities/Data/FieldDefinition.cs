// Moved from blocks-data server/DataGateway.DomainService/Entities/FieldDefinition.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Data;

[BsonIgnoreExtraElements(Inherited = true)]
public class FieldDefinition //: FieldAccessInformation
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsArray { get; set; }
    public List<string> EnumValues { get; set; } = [];
    public bool IsPIIData { get; set; }
    public bool IsUniqueData { get; set; }
    public string Description { get; set; } = string.Empty;
    [BsonRepresentation(BsonType.String)]
    public RequiredOn RequiredOn { get; set; } = RequiredOn.None;
    public bool IsReferenceField { get; set; }
    public string ReferenceFieldType { get; set; } = string.Empty;
    public SchemaAccessLevel ReadAccessLevel { get; set; } = SchemaAccessLevel.Inherited;
    public SchemaAccessLevel WriteAccessLevel { get; set; } = SchemaAccessLevel.Inherited;
    public SchemaAccessLevel EditAccessLevel { get; set; } = SchemaAccessLevel.Inherited;
    public SchemaAccessLevel DeleteAccessLevel { get; set; } = SchemaAccessLevel.Inherited;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequiredOn
{
    None,
    Insert,
    Update,
    Both
}
