// Moved from blocks-release server/Devops.DomainService/Deployment/Entities/Build.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Release;

[BsonIgnoreExtraElements]
public class Build : BaseEntity
{
    public string ProjectId { get; set; }
    public string ProjectName { get; set; }
    public string BlocksUserId { get; set; }
    public string RepoId { get; set; }
    public string RepoName { get; set; }
    public string RepoUrl { get; set; }
    public string DefaultDeploymentUrl { get; set; }
    public string CustomDeploymentUrl { get; set; }
    // Repo.IsCustomDomainOnly as it was when this build was started.
    public bool IsCustomDomainOnly { get; set; }
    public string Commit { get; set; }
    public string Branch { get; set; }
    public string ImageName { get; set; }
    public string Status { get; set; } = "Started"; // EventStatus.STARTED (blocks-release PipeLineTaskConstants)
    public string EventName { get; set; } = "Pipeline"; // EventNames.PIPELINE (blocks-release PipeLineTaskConstants)
    public long Duration { get; set; }
    public string BuildImageName { get; set; }
    public string PipelineRunName { get; set; }
    public string Html_url { get; set; }
    public string DependencyTrackProjectId { get; set; }
    public List<BuildEventResponse> Events { get; set; }

    // SonarQube and Dependency-Track results saved for this build. Null on builds from before they were saved.
    [BsonIgnoreIfNull]
    public SastReport? SastReport { get; set; }
    [BsonIgnoreIfNull]
    public ScaReport? ScaReport { get; set; }
}
