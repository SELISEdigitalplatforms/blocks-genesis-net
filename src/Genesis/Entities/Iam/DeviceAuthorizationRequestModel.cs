// Moved from blocks-iam server/Authentication.DomainService/Oidc/Contracts/DeviceAuthorizationContracts.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Iam;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements]
public sealed class DeviceAuthorizationRequestModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string DeviceCodeHash { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string RequestedScopes { get; set; } = string.Empty;
    public string Status { get; set; } = DeviceAuthorizationStatus.Pending;
    public string? UserId { get; set; }
    public string? OrganizationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? DeniedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime LastPollAt { get; set; } = DateTime.UtcNow;
    public int PollIntervalSeconds { get; set; } = 5;
    public int PollsObserved { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceInfo { get; set; }
    public string? ApprovalTokenHash { get; set; }
}

public static class DeviceAuthorizationStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Denied = "Denied";
    public const string Consumed = "Consumed";
    public const string Expired = "Expired";
}
