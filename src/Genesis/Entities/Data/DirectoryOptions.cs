// Moved from blocks-data server/Storage.DomainService/Shared/Entities/DirectoryOptions.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Data;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class DirectoryOptions
{
    public string Name { get; set; }
    public string ParentId { get; set; }
    public Dictionary<string, MetaValue> MetaData { get; set; }
    public string ItemId { get; set; }
    public string TenantId { get; set; }
    public DateTime CreateDate { get; set; }
    public string CreatedBy { get; set; }
    public List<string> Tags { get; set; }
    public string Language { get; set; }
    public string[] AllowedFileExtensions { get; set; }
    public List<string>? AncestorIds { get; set; }
    public string? FullPath { get; set; }
    public bool InheritsParentAccess { get; set; } = true;
    public string? ConfigurationName { get; set; }
    public string? ModuleName { get; set; }
    public string? Description { get; set; }
    public ObjectAccessLevel? ObjectAccessLevel { get; set; }
}
