using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Where the resolver takes a schema's policies from. The seam for future policy sources; the only
/// implementation today is the list already assembled onto the schema from the published snapshot.
/// </summary>
public interface IAccessPolicySource
{
    IReadOnlyList<DataAccessPolicy> GetPolicies(SchemaDefinitionExtended schema);
}

/// <summary>The policies the schema builder attached to <see cref="SchemaDefinitionExtended.Policies"/>.</summary>
public sealed class SchemaPolicySource : IAccessPolicySource
{
    public IReadOnlyList<DataAccessPolicy> GetPolicies(SchemaDefinitionExtended schema) => schema.Policies;
}
