// Moved from blocks-os server/Configuration.DomainService/Mail/Entities/EmailTemplate.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Os;

[BsonIgnoreExtraElements]
public class EmailTemplate : BaseEntity
{
    public string? MailConfigurationId { get; set; }
    public string? TemplateBody { get; set; }
    public string? JsonContent { get; set; }
    public string? ImageId { get; set; }
    public string? ImageUrl { get; set; }
    public string? Name { get; set; }
    public string? TemplateSubject { get; set; }
    public string? GeneratedBy { get; set; }
}
