// Moved from blocks-release server/Devops.DomainService/Deployment/Entities/RepositoryWebhook.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;

namespace Blocks.Genesis.Entities.Release;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class RepositoryWebhook : BaseEntity
{
    public string RepoId { get; set; }
    public string RepoUrl { get; set; }

    public string Ref { get; set; }
    public string BeforeSha { get; set; }
    public string AfterSha { get; set; }
    public string HeadCommitSha { get; set; }
    public string HeadCommitMessage { get; set; }
    public string PusherName { get; set; }
    public string PusherEmail { get; set; }
    public DateTime HeadCommitTimestamp { get; set; }
    public string HeadCommitUrl { get; set; }

    public string AuthorName { get; set; }
    public string AuthorEmail { get; set; }

    public string CommitterName { get; set; }
    public string CommitterEmail { get; set; }
}
