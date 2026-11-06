// Moved from blocks-release server/Devops.DomainService/Deployment/Entities/Repo.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;
using Blocks.Genesis.DataAccess.Rules;

namespace Blocks.Genesis.Entities.Release;

[BsonIgnoreExtraElements]
[SystemSchemaRules(RuleLevel.Warned)]
public class Repo : BaseEntity
{
    public string SourceRepoId { get; set; }
    public string SourceReference { get; set; }
    public string BlocksUserId { get; set; }
    public string ProjectId { get; set; }
    public string ProjectName { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string RepoName { get; set; }
    public string RepoUrl { get; set; }
    public string DefaultDeploymentUrl { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string CustomDeploymentUrl { get; set; }
    // True deploys to CustomDeploymentUrl alone. False (the default, and absent on older documents) deploys to
    // both domains. Ignored while CustomDeploymentUrl is empty, so a repo is never left without a domain.
    public bool IsCustomDomainOnly { get; set; }
    // The exact `namespace` param sent to Tekton at deploy time. Null/absent means no live deployment.
    // Never recomputed - it is the only value the delete path is allowed to act on.
    public string DeployedNamespace { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string Branch { get; set; }
    public string Commit { get; set; }
    public DateTime? LastDeploymentDate { get; set; }
    public string LastDeploymentStatus { get; set; }
    public DeploySettings DeploySettings { get; set; }
    public string DeploymentType { get; set; }
    public GithubWebhook GithubWebhook { get; set; }
    public string RepositoryType { get; set; }
    public string DependencyTrackProjectUuid { get; set; }
    public bool IsArchived { get; set; }

    // ItemId of this repository's secret in the SecretStore database. Null means no secrets
    // have ever been saved. Only ever written by RepoRepository.UpdateRepoSecretStoreItemId -
    // deliberately absent from RepoUpdateRequest, so a settings save can never clear it and
    // orphan the vault value it points at. Retained after a soft delete so a restore can
    // still reach the secret.
    public string SecretStoreItemId { get; set; }
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class GithubWebhook
{
    public long Id { get; set; }
    public string Name { get; set; }
    public bool Active { get; set; }
    public List<string> Events { get; set; }
    public Config Config { get; set; }
}

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class Config
{
    public string ContentType { get; set; }
    public string InsecureSsl { get; set; }
    public string Url { get; set; }
}
