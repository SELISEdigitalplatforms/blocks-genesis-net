// Moved from blocks-iam server/Iam.DomainService/Shared/Dtos/DeviceInformation.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Iam;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class DeviceInformation
{
    public string Browser { get; set; }
    public string OS { get; set; }
    public string Device { get; set; }
    public string Brand { get; set; }
    public string Model { get; set; }
}
