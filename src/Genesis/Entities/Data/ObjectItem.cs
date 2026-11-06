// Moved from blocks-data server/Storage.DomainService/Shared/Entities/ObjectItem.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Data;

/// <summary>
/// Denormalized read model for object views. Files and directories remain the
/// authoritative write entities; each has exactly one ObjectItem projection,
/// keyed back to its source via <see cref="ObjectReferenceId"/>. ItemId (from
/// BaseEntity) is the projection's own identity, not the source File/FileDirectory id.
/// </summary>
[BsonIgnoreExtraElements]
public class ObjectItem : BaseEntity
{
    /// <summary>The ItemId of the source File or FileDirectory this projection was built from.</summary>
    public string ObjectReferenceId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public StructureType Type { get; set; }
    public string? ParentDirectoryId { get; set; }
    public List<string> AncestorIds { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public bool IsActive { get; set; } = true;
    public bool InheritsParentAccess { get; set; } = true;
    public long SizeInBytes { get; set; }
    public string? Extension { get; set; }
    public string? ContentType { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>Mirrors the source File/FileDirectory's default access when unshared.</summary>
    [BsonRepresentation(BsonType.String)]
    public ObjectAccessLevel? ObjectAccessLevel { get; set; }

    public static ObjectItem From(File file) => new()
    {
        ItemId = file.ItemId,
        ObjectReferenceId = file.ItemId,
        TenantId = file.TenantId,
        OrganizationId = file.OrganizationId,
        Type = StructureType.File,
        ParentDirectoryId = string.IsNullOrWhiteSpace(file.DirectoryId) ? null : file.DirectoryId,
        AncestorIds = file.AncestorIds ?? new(),
        Name = file.Name ?? string.Empty,
        IsArchived = file.IsArchived,
        IsActive = file.IsActive,
        InheritsParentAccess = file.InheritsParentAccess,
        CreatedBy = file.CreatedBy ?? string.Empty,
        SizeInBytes = file.SizeInBytes,
        Extension = file.Extension,
        ContentType = file.ContentType,
        CreatedDate = file.CreatedDate,
        LastUpdatedDate = file.LastUpdatedDate,
        ObjectAccessLevel = file.ObjectAccessLevel,
    };

    public static ObjectItem From(FileDirectory directory) => new()
    {
        ItemId = directory.ItemId,
        ObjectReferenceId = directory.ItemId,
        TenantId = directory.TenantId,
        OrganizationId = directory.OrganizationId,
        Type = StructureType.Directory,
        ParentDirectoryId = string.IsNullOrWhiteSpace(directory.ParentId) ? null : directory.ParentId,
        AncestorIds = directory.AncestorIds ?? new(),
        Name = directory.Name ?? string.Empty,
        FullPath = directory.FullPath ?? string.Empty,
        IsArchived = directory.IsArchived,
        IsActive = directory.IsActive,
        InheritsParentAccess = directory.InheritsParentAccess,
        CreatedBy = directory.CreatedBy ?? string.Empty,
        SizeInBytes = directory.SizeInBytes,
        IsDefault = directory.Tags?.Contains("default", StringComparer.OrdinalIgnoreCase) == true,
        CreatedDate = directory.CreatedDate,
        LastUpdatedDate = directory.LastUpdatedDate,
        ObjectAccessLevel = directory.ObjectAccessLevel,
    };
}
