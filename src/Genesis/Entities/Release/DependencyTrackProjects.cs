// Moved from blocks-release server/Devops.DomainService/AnalyticsTool/Entity/DependencyTrackProjects.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;

namespace Blocks.Genesis.Entities.Release;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class DependencyTrackProjects : BaseEntity
{
    public string ProjectName { get; set; }
    public string ProjectId { get; set; }
    public string ProjectTeamName { get; set; }
    public string ProjectTeamUuid { get; set; }
    public List<RepoProject> RepoProjects { get; set; }
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class RepoProject
{
    public string RepoId { get; set; }
    public string ProjectUuid { get; set; }
}
