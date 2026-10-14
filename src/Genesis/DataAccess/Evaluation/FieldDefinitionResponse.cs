using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

public class FieldDefinitionResponse
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsArray { get; set; }
    public List<string> EnumValues { get; set; } = [];
    public bool IsPIIData { get; set; }
    public bool IsUniqueData { get; set; }
    public string Description { get; set; }
    public RequiredOn RequiredOn { get; set; } = RequiredOn.None;
	public List<FieldDefinitionResponse> Fields { get; set; } = [];
    public SchemaAccessLevel ReadAccessLevel { get; set; }
    public SchemaAccessLevel WriteAccessLevel { get; set; }
    public SchemaAccessLevel EditAccessLevel { get; set; }
    public SchemaAccessLevel DeleteAccessLevel { get; set; }
    public DataValidation? ValidationRule { get; set; } = null;

    public int TotalValidationRules => ValidationRule?.Validations.Count ?? 0;
    public int TotalReadPolicies { get; set; }
    public int TotalWritePolicies { get; set; }
    public int TotalEditPolicies { get; set; }
    public int TotalDeletePolicies { get; set; }
}
