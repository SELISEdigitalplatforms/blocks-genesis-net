// Moved from blocks-iam server/Iam.DomainService/Shared/Entities/Organization.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
[SystemSchemaRules(RuleLevel.Warned)]
public class Organization : BaseEntity
{
    [AllowRules(Validation = RuleAccess.Warned)]
    public required string Name { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? Description { get; set; }
    public string? ParentOrganizationId { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string? ShortCode { get; set; }
    public bool IsDisabled { get; set; }
    public List<string> DefaultRoleForMembers { get; set; } = new List<string>();
    public List<string> DefaultPermissionsForMembers { get; set; } = new List<string>();

    // Contact
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? Email { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? PhoneNumber { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? WebsiteUrl { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public List<Address> Addresses { get; set; } = new List<Address>();

    // Branding
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public Theme? Theme { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? LogoUrl { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? LogoId { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? Industry { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string TimeZone { get; set; } = "UTC";
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? Currency { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    [AllowRules(Validation = RuleAccess.Warned)]
    public string TimeFormat { get; set; } = "HH:mm";
    [AllowRules(Validation = RuleAccess.Warned)]
    public string Locale { get; set; } = "en-US";

    // Add custom attributes to the organization
    [BsonSerializer(typeof(AttributeBagSerializer))]
    public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
}

[BsonIgnoreExtraElements]
public class Theme
{
    public string? Name { get; set; }
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }
    public string? TertiaryColor { get; set; }
    [BsonSerializer(typeof(AttributeBagSerializer))]
    public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
}

[BsonIgnoreExtraElements]
public class Address
{
    public string Name { get; set; } = string.Empty;
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public bool IsPrimary { get; set; }
}
