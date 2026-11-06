// Moved from blocks-iam server/Mfa.DomainService/Configuration/MfaConfiguration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public class MfaConfiguration : BaseEntity
{
    public string? Name { get; set; } = "Default";
    public bool EnableMfa { get; set; }
    public List<UserMfaType> UserMfaTypes { get; set; } = [];
    public MfaTemplate? MfaTemplate { get; set; } = new();
    public bool RequireMfaForAllUsers { get; set; }
    public List<string> MfaRequiredRoles { get; set; } = [];
    public List<string> MfaExemptRoles { get; set; } = [];
    public bool AllowUserOptOut { get; set; } = true;
    public bool AllowBackupCodes { get; set; } = true;
    public int BackupCodesCount { get; set; } = 10;
}
