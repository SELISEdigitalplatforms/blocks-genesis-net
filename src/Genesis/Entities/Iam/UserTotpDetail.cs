// Moved from blocks-iam server/Mfa.DomainService/TOTP/UserTotpDetail.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Iam;

[BsonIgnoreExtraElements]
public class UserTotpDetail : BaseEntity
{
    public string? ImageUri { get; set; }
    public string? TowFactorId { get; set; }
    public string? Secret { get; set; }

    /// <summary>
    /// The Blocks Secrets id of the TOTP secret. When set, the value lives only in the secret store and
    /// <see cref="Secret"/> is empty; a document without it holds an old plaintext secret. Not stored when null.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? SecretReference { get; set; }
}
