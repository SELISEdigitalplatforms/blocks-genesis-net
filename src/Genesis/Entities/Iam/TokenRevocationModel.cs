// Moved from blocks-iam server/Authentication.DomainService/Oidc/Repositories/TokenRevocationRepository.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;

namespace Blocks.Genesis.Entities.Iam;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public sealed class TokenRevocationModel
{
    public string? Id { get; set; } // MongoDB ObjectId
    public string? Jti { get; set; } // JWT ID (unique token identifier)
    public string? UserId { get; set; }
    public DateTime RevokedAt { get; set; }
    public string? RevokeReason { get; set; } // "user_revoked", "logout", "reuse_detected", "password_changed"
    public DateTime ExpiresAt { get; set; } // After this date, can be deleted from DB
    public bool IsDeleted { get; set; } // Soft delete flag
    public DateTime DeletedAt { get; set; } // Soft delete timestamp
}
