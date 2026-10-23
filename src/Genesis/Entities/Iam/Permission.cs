// Moved from blocks-iam server/Iam.DomainService/Shared/Entities/Permission.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
[SystemSchemaRules(RuleLevel.Warned)]
public class Permission : BuiltInPermission
{
    public List<string> Roles { get; set; } = new List<string>();

    // Superset fields (genesis SPEC1 6.5, rule A-B2): blocks-os writes them with a $set
    // (Cloud.DomainService/Repositories/ApiEndpointConfigRepository.cs UpdateAsync/BulkUpdateAsync).
    // Nullable + [BsonIgnoreIfNull]: iam's own writes stay byte-identical. [JsonIgnore]: iam's API,
    // queue and Redis JSON stay exactly as today; os maps them into its own response.
    [BsonIgnoreIfNull]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? IsCaptchaRequired { get; set; }

    [BsonIgnoreIfNull]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? IsMFARequired { get; set; }
   // public string OrganizationId { get; set; } = "default";
}
