// Moved from blocks-data server/DataGateway.DomainService/Entities/DataValidation.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Data;

/// <summary>
/// Entity to store field validations for a schema
/// </summary>
[BsonIgnoreExtraElements]
public class DataValidation : GraphQlBaseEntity
{
    /// <summary>
    /// The schema ID this validation belongs to
    /// </summary>
    public string SchemaId { get; set; } = string.Empty;

    /// <summary>
    /// The field name being validated
    /// </summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>
    /// List of validation rules for this field
    /// </summary>
    public List<ValidationRule> Validations { get; set; } = new List<ValidationRule>();
}

/// <summary>
/// Represents a single validation rule
/// </summary>
[BsonIgnoreExtraElements]
public class ValidationRule
{
    /// <summary>
    /// Type of validation (NotEmpty, Regex, MinLength, MaxLength, LengthRange, Equal, NotEqual, GreaterThan, LessThan, GreaterThanOrEqual, LessThanOrEqual, Range, Reference)
    /// </summary>
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public ValidationType Type { get; set; }

    /// <summary>
    /// The value to validate against (can be string, number, date depending on validation type)
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Secondary value for range validations (e.g., max value in range)
    /// </summary>
    public object? SecondaryValue { get; set; }

    /// <summary>
    /// Custom error message for this validation
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether this validation is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// How the rule is evaluated. 0 (every rule saved before this field existed): a rule on a list
    /// property checks the list as a whole, exactly as before. 1: a list property is checked item by item.
    /// Not written while 0, so existing documents are unchanged.
    /// </summary>
    [BsonIgnoreIfDefault]
    public int EvaluationVersion { get; set; }

    /// <summary>
    /// "Only when": the rule applies only to a record that matches this condition, in the policy condition
    /// model (left side a property of the record). Null: the rule always applies.
    /// Not written while null, so existing documents are unchanged.
    /// </summary>
    [BsonIgnoreIfNull]
    public PolicyRuleGroup? When { get; set; }

    /// <summary>
    /// For Reference and NotExistsIn: the target row must also match this condition (left side a property of
    /// the target schema, right side a fixed value). Null: any target row counts.
    /// Not written while null, so existing documents are unchanged.
    /// </summary>
    [BsonIgnoreIfNull]
    public PolicyRuleGroup? TargetFilter { get; set; }
}
