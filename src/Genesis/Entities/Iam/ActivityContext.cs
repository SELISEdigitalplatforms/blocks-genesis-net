// Moved from blocks-iam server/Iam.DomainService/Shared/Dtos/ActivityContext.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Iam;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public sealed class ActivityContext
{
    public string? IpAddress { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceType { get; set; }
    public string? UserAgent { get; set; }
    public DeviceInformation? DeviceInformation { get; set; }
}
