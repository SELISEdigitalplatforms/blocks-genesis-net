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
    /// Type of validation (NotEmpty, Regex, MinLength, MaxLength, Range, Equal, NotEqual, GreaterThan, LessThan, GreaterThanOrEqual, LessThanOrEqual)
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
}
