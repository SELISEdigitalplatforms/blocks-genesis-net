// Moved from blocks-os server/Configuration.DomainService/Integration/Entities/IntegrationRequest.cs (dev working tree, 2026-10-15).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Os;

[BsonIgnoreExtraElements]
public sealed class IntegrationRequest : BaseEntity
{
    public string Status { get; set; } = "pending";
    public string Family { get; set; } = string.Empty;
    public string? SuggestedTemplateKey { get; set; }
    public string RedirectUri { get; set; } = string.Empty;
    public string RedirectHost { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string CodeChallenge { get; set; } = string.Empty;
    public string CodeChallengeMethod { get; set; } = "S256";
    public string SiteName { get; set; } = string.Empty;
    public string RootTenantId { get; set; } = string.Empty;
    public string? ClaimedByUserId { get; set; }
    public string? EnvironmentTenantId { get; set; }
    public string? TemplateKey { get; set; }
    public string? ConnectionId { get; set; }
    // Left out of the document until approval: the unique index on CodeHash must never see a
    // null, or every pending request after the first collides on it.
    [BsonIgnoreIfNull]
    public string? CodeHash { get; set; }
    public string? ClientId { get; set; }
    public string? SecretCipher { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public DateTime? ExchangedDate { get; set; }
}
