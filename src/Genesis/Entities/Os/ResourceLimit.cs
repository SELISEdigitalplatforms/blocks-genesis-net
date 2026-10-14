// Moved from blocks-os server/Identifier.DomainService/Shared/Entities/ResourceLimit.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Os;

[BsonIgnoreExtraElements]
public class ResourceLimit : BaseEntity
{
    public string Resource { get; set; }
    public string ResourceType { get; set; }
    public int Limit { get; set; }
    public int Usage { get; set; }
    public DateTime Lifetime { get; set; }
    public bool IsActive { get; set; }
    public bool EnableAutoRenew { get; set; }
    public string TenantId { get; set; }
    public string Type { get; set; }
}
