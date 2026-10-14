// Moved from blocks-data server/Storage.DomainService/Shared/Entities/FileDirectory.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Data;

[BsonIgnoreExtraElements]
public class FileDirectory : Structure
{
    public string TenantId { get; set; }

    /// <summary>Default access when unshared. Null preserves pre-existing (allow-all) behaviour.</summary>
    [BsonRepresentation(BsonType.String)]
    public ObjectAccessLevel? ObjectAccessLevel { get; set; }

    /// <summary>Cached directory ancestry, ordered root first, ending at the parent directory.</summary>
    public List<string> AncestorIds { get; set; } = new();

    /// <summary>Display and search path built from the ancestry, for example "/root/sub/parent".</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>When true the effective access policy is resolved by walking <see cref="AncestorIds"/>.</summary>
    public bool InheritsParentAccess { get; set; } = true;

    /// <summary>Soft delete. Archived directorys stay queryable so they can be listed in trash and restored.</summary>
    public bool IsArchived { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ConfigurationName { get; set; }
    public string? ModuleName { get; set; }
    public string? Description { get; set; }

    /// <summary>Cached counts and subtree size, maintained on write so listing does not aggregate.</summary>
    public int ChildDirectoryCount { get; set; }
    public int ChildFileCount { get; set; }
    public long SizeInBytes { get; set; }

    public static FileDirectory CreateNew(DirectoryOptions directoryOptions)
    {
        return new FileDirectory
        {
            Name = directoryOptions.Name,
            ParentId = string.IsNullOrEmpty(directoryOptions.ParentId) ? null : directoryOptions.ParentId,
            SystemName = directoryOptions.Name.ToLower(),
            Type = StructureType.Directory,
            TypeString = StructureType.Directory.ToString(),
            MetaData = directoryOptions.MetaData,
            ItemId = directoryOptions.ItemId,
            TenantId = directoryOptions.TenantId,
            CreatedDate = directoryOptions.CreateDate,
            CreatedBy = directoryOptions.CreatedBy,
            // A newly created directory has not been modified after creation, so both
            // timestamps (and their actors) must describe the same initial write.
            LastUpdatedDate = directoryOptions.CreateDate,
            LastUpdatedBy = directoryOptions.CreatedBy,
            Tags = directoryOptions.Tags,
            Language = directoryOptions.Language,
            AllowedFileExtensions = directoryOptions.AllowedFileExtensions,
            AncestorIds = directoryOptions.AncestorIds ?? new(),
            FullPath = directoryOptions.FullPath ?? string.Empty,
            InheritsParentAccess = directoryOptions.InheritsParentAccess,
            ConfigurationName = directoryOptions.ConfigurationName,
            ModuleName = directoryOptions.ModuleName,
            Description = directoryOptions.Description,
            ObjectAccessLevel = directoryOptions.ObjectAccessLevel,
        };
    }
}
