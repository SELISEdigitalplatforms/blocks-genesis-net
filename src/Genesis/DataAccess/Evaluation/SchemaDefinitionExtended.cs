using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

public class SchemaDefinitionExtended : SchemaDefinition
{
    public new List<FieldDefinitionResponse> Fields { get; set; } = [];
    public List<DataAccessPolicy> Policies { get; set; } = [];

    /// <summary>
    /// A shallow copy of this schema (fields and every other property shared) with <paramref name="policies"/> as its
    /// policies. Used for per-request copies with resolved lookups; this instance is left unchanged.
    /// </summary>
    public SchemaDefinitionExtended WithPolicies(List<DataAccessPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var copy = (SchemaDefinitionExtended)MemberwiseClone();
        copy.Policies = policies;
        return copy;
    }
}
