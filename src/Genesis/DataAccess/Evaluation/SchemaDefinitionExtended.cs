using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

public class SchemaDefinitionExtended : SchemaDefinition
{
    public new List<FieldDefinitionResponse> Fields { get; set; } = [];
    public List<DataAccessPolicy> Policies { get; set; } = [];
}
