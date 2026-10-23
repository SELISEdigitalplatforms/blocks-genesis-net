// Moved from blocks-iam server/Iam.DomainService/Shared/Entities/BuiltInPermission.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public class BuiltInPermission : BaseEntity
{
    [AllowRules(Validation = RuleAccess.Warned)]
    public string Name { get; set; }
    public ResourceType Type { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string Description { get; set; }
    public string Resource { get; set; }
    public string ResourceGroup { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool IsArchived { get; set; }
    public PermissionSeverity PermissionSeverity { get; set; }
    public List<string> DependentPermissions { get; set; } = [];
}
