// Moved from blocks-os server/Configuration.DomainService/Integration/Entities/IntegrationSetup.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis.Entities.Os;

/// <summary>
/// The record that a project has completed Integration setup, and what that setup created.
/// </summary>
/// <remarks>
/// Stored in the tenant's own database, at most one per template (a unique index on
/// <see cref="TemplateKey"/>). Only references are kept: the client secret stays with IAM's
/// credential and is read from there when shown.
/// </remarks>
[BsonIgnoreExtraElements]
[SystemSchemaRules(RuleLevel.Warned)]
public class IntegrationSetup : BaseEntity
{
    public string TemplateKey { get; set; } = string.Empty;

    [AllowRules(Validation = RuleAccess.Allowed)]
    public string TemplateDisplayName { get; set; } = string.Empty;

    public string RoleId { get; set; } = string.Empty;

    public string RoleSlug { get; set; } = string.Empty;

    /// <summary>The IAM client credential id, which is also the client id.</summary>
    public string ClientCredentialId { get; set; } = string.Empty;

    [AllowRules(Validation = RuleAccess.Allowed)]
    public string ConnectionName { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public string Status { get; set; } = "active";
    [AllowRules(Validation = RuleAccess.Warned)]
    public string? SiteUrl { get; set; }
    public bool NeverDelivered { get; set; }
    public string? RevokedBy { get; set; }
    public DateTime? RevokedDate { get; set; }
    public string TemplateAccessLevel { get; set; } = string.Empty;
}
