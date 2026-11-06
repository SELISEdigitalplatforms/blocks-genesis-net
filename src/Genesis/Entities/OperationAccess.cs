using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis;

/// <summary>
/// Who may perform one operation (create, read, update or delete) on a record: any of the listed roles,
/// users or permissions. Used by <see cref="BaseEntity.Create"/>, <see cref="BaseEntity.Read"/>,
/// <see cref="BaseEntity.Update"/> and <see cref="BaseEntity.Delete"/>.
/// </summary>
[BsonIgnoreExtraElements]
public class OperationAccess
{
    public string[] Roles { get; set; } = [];
    public string[] UserIds { get; set; } = [];
    public string[] Permissions { get; set; } = [];
}
