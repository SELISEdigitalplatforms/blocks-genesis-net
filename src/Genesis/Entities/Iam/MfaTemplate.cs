// Moved from blocks-iam server/Mfa.DomainService/Configuration/Configuration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Iam;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class MfaTemplate
{
    public string? TemplateName { get; set; }
    public string? TemplateId { get; set; }
}
