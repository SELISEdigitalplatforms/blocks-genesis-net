// Moved from blocks-release server/Devops.DomainService/Deployment/Models/Dtos/DeploySettings.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Release;

[BsonIgnoreExtraElements]
public class DeploySettings
{
    public HostingProvider HostingProvider { get; set; }
    public Region Region { get; set; }
    public MachineConfig MachineConfig { get; set; }
}
