// Moved from blocks-os server/Configuration.DomainService/Mail/Entities/EmailTemplate.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis.Entities.Os;

[BsonIgnoreExtraElements]
[SystemSchemaRules(RuleLevel.Normal)]
public class EmailTemplate : BaseEntity
{
    public string? MailConfigurationId { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string? TemplateBody { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string? JsonContent { get; set; }
    public string? ImageId { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? ImageUrl { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string? Name { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? TemplateSubject { get; set; }
    public string? GeneratedBy { get; set; }
}
