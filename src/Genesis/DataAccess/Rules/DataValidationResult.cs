// Moved from blocks-data server/DataGateway.DomainService/Models/Responses/DataValidationResult.cs (dev 3664eb77) so the gateway and the platform services share one rules engine.
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// Result of data validation containing validation errors
/// </summary>
public class DataValidationResult
{
    /// <summary>
    /// Whether all validations passed
    /// </summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// List of validation errors
    /// </summary>
    public List<ValidationError> Errors { get; set; } = new List<ValidationError>();

    /// <summary>
    /// Combined error message from all errors
    /// </summary>
    public string ErrorMessage => Errors.Count > 0
        ? string.Join("; ", Errors.Select(e => e.Message))
        : string.Empty;

    /// <summary>
    /// Add a validation error
    /// </summary>
    public void AddError(string fieldName, string message, string validationType)
    {
        Errors.Add(new ValidationError
        {
            FieldName = fieldName,
            Message = message,
            ValidationType = validationType
        });
    }
}

/// <summary>
/// Represents a single validation error
/// </summary>
public class ValidationError
{
    /// <summary>
    /// The field that failed validation
    /// </summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>
    /// Error message describing the validation failure
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The type of validation that failed
    /// </summary>
    public string ValidationType { get; set; } = string.Empty;
}

/// <summary>
/// Thrown by a service's data-access wrapper when a write breaks the schema's validation rules.
/// Each service maps it to its own validation-error response (HTTP 400) with <see cref="Result"/>'s errors.
/// </summary>
public sealed class DataValidationException : Exception
{
    public DataValidationException(DataValidationResult result) : base(result.ErrorMessage)
    {
        Result = result;
    }

    public DataValidationResult Result { get; }
}
