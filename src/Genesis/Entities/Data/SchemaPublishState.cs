// Moved from blocks-data server/DataGateway.DomainService/Entities/SchemaPublishState.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Data;

/// <summary>
/// The tenant's published schema version, shared by every gateway pod. There is exactly one
/// document per tenant database (<see cref="StateId"/>). Each pod compares the version it built
/// against <see cref="CurrentVersion"/> and rebuilds its GraphQL schema when it is behind.
/// </summary>
[BsonIgnoreExtraElements]
public class SchemaPublishState : GraphQlBaseEntity
{
    public const string StateId = "state";

    /// <summary>
    /// The version that is live: the <see cref="PublishedSchemaSnapshot"/> every pod serves. A
    /// tenant that never published is at version 0. Publishing moves it forward; a rollback can
    /// move it back to an older snapshot.
    /// </summary>
    public long CurrentVersion { get; set; }

    /// <summary>
    /// The highest version number ever handed out. Only publishing changes it, and it only goes up,
    /// so a publish after a rollback gets a new number instead of reusing an existing one.
    /// </summary>
    public long LastAllocatedVersion { get; set; }
}
