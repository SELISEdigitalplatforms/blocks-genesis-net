// Moved from blocks-logic server/Notification.DomainService/Configuration/FirebaseConfiguration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;

namespace Blocks.Genesis.Entities.Logic;

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class FirebaseConfiguration : BaseEntity
{
    public string AuthorizationKey { get; set; }
}
