// Moved from blocks-iam server/Authentication.DomainService/Shared/Entities/ClientCredential.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public class ClientCredential : BaseEntity
{
    public string? Name { get; set; }
    public string? ClientSecret { get; set; }
    public int AccessTokenValidForNumberMinutes { get; set; } = 5;
    public List<string> Roles { get; set; } = new List<string>();
    public List<string> Permissions { get; set; } = new List<string>();
    public bool IsActive { get; set; }
}
