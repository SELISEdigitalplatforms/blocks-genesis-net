// Moved from blocks-data server/DataGateway.DomainService/Helpers/DataValidationHelper.cs (dev 3664eb77) so the gateway and the platform services share one rules engine.
using Blocks.Genesis.DataAccess.Evaluation;
using System.Text.RegularExpressions;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// Static helper class for validating data against validation rules
/// </summary>
public static class DataValidationHelper
{
    /// <summary>
    /// Comparison types for numeric and date validations
    /// </summary>
    private enum ComparisonType
    {
        GreaterThan,
        LessThan,
        GreaterThanOrEqual,
        LessThanOrEqual
    }

    /// <summary>
    /// Validates input data against the schema's validation rules.
    /// Supports nested objects: when a field is not a scalar type, validation recurses into the nested value.
    /// </summary>
    /// <param name="input">The input data dictionary (can contain nested dictionaries or lists of dictionaries)</param>
    /// <param name="schema">The schema definition with validation rules</param>
    /// <returns>Validation result containing any errors</returns>
    public static DataValidationResult Validate(this Dictionary<string, object?> input, SchemaDefinitionExtended schema) =>
        input.Validate(schema, storedRecord: null);

    /// <summary>
    /// Validates input; a version-1 comparison rule whose value is a variable (<c>{{Employee.StartDate}}</c>)
    /// reads it from the input, then from <paramref name="storedRecord"/> (the record being updated).
    /// </summary>
    public static DataValidationResult Validate(this Dictionary<string, object?> input, SchemaDefinitionExtended schema, IReadOnlyDictionary<string, object?>? storedRecord)
    {
        var result = new DataValidationResult();
        ValidateData(input, schema.Fields, result, pathPrefix: "", new VariableScope(input, storedRecord));
        return result;
    }

    /// <summary>The whole record a variable reads from: the input (root), then the stored record.</summary>
    private sealed record VariableScope(IReadOnlyDictionary<string, object?> Root, IReadOnlyDictionary<string, object?>? Stored);

    private static readonly HashSet<ValidationType> VariableRuleTypes =
    [
        ValidationType.Equal, ValidationType.NotEqual, ValidationType.GreaterThan, ValidationType.LessThan,
        ValidationType.GreaterThanOrEqual, ValidationType.LessThanOrEqual, ValidationType.Range,
    ];

    /// <summary>
    /// The rule with any variable values replaced by what they read; null when a variable has no value,
    /// so the rule is skipped. Only version-1 rules have variables: an older rule's text stays text.
    /// </summary>
    private static ValidationRule? WithVariablesResolved(ValidationRule rule, string fieldPath, VariableScope scope)
    {
        if (rule.EvaluationVersion < 1 || !VariableRuleTypes.Contains(rule.Type))
            return rule;

        var hasValueVariable = VariableSyntax.TryGetSingleVariable(rule.Value, out var valueVariable);
        var hasSecondaryVariable = VariableSyntax.TryGetSingleVariable(rule.SecondaryValue, out var secondaryVariable);
        if (!hasValueVariable && !hasSecondaryVariable)
            return rule;

        var value = hasValueVariable ? VariableSyntax.Resolve(valueVariable, scope.Root, scope.Stored, null) : rule.Value;
        var secondary = hasSecondaryVariable ? VariableSyntax.Resolve(secondaryVariable, scope.Root, scope.Stored, null) : rule.SecondaryValue;
        if (value is null || (rule.Type == ValidationType.Range && secondary is null))
            return null;

        string Show(bool isVariable, VariablePart variable, object? resolved) =>
            isVariable ? $"{variable.Name} ({Display(resolved)})" : Display(resolved);

        var message = rule.ErrorMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            var bound = Show(hasValueVariable, valueVariable, value);
            message = rule.Type switch
            {
                ValidationType.Equal => $"{fieldPath} must be equal to {bound}",
                ValidationType.NotEqual => $"{fieldPath} must be different from {bound}",
                ValidationType.GreaterThan => $"{fieldPath} must be greater than {bound}",
                ValidationType.LessThan => $"{fieldPath} must be less than {bound}",
                ValidationType.GreaterThanOrEqual => $"{fieldPath} must be greater than or equal to {bound}",
                ValidationType.LessThanOrEqual => $"{fieldPath} must be less than or equal to {bound}",
                _ => $"{fieldPath} must be between {bound} and {Show(hasSecondaryVariable, secondaryVariable, secondary)}",
            };
        }

        return new ValidationRule
        {
            Type = rule.Type,
            Value = value,
            SecondaryValue = secondary,
            ErrorMessage = message,
            IsActive = rule.IsActive,
            EvaluationVersion = rule.EvaluationVersion,
        };
    }

    private static string Display(object? value) => value switch
    {
        DateTime date when date.TimeOfDay == TimeSpan.Zero => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// Validates input against a list of field definitions and appends errors to the result.
    /// Recurses into nested objects when a field is not a scalar type.
    /// </summary>
    private static void ValidateData(
        Dictionary<string, object?> input,
        List<FieldDefinitionResponse> fields,
        DataValidationResult result,
        string pathPrefix,
        VariableScope scope)
    {
        foreach (var fieldKey in input.Keys)
        {
            var fieldDefinition = fields.FirstOrDefault(f => f.Name == fieldKey);
            if (fieldDefinition is null)
            {
                continue; // Skip unknown fields
            }

            var fieldValue = input[fieldKey];
            var fieldPath = string.IsNullOrEmpty(pathPrefix) ? fieldKey : $"{pathPrefix}{fieldKey}";

            if (!SchemaTypeHelper.IsScalar(fieldDefinition.Type))
            {
                // Nested type: validate the nested object(s)
                if (fieldValue is null)
                {
                    continue;
                }

                if (fieldDefinition.IsArray && fieldValue is System.Collections.IEnumerable list && fieldValue is not string)
                {
                    var index = 0;
                    foreach (var item in list)
                    {
                        if (item is Dictionary<string, object?> nestedDict && fieldDefinition.Fields.Count > 0)
                        {
                            ValidateData(nestedDict, fieldDefinition.Fields, result, $"{fieldPath}[{index}].", scope);
                        }
                        index++;
                    }
                }
                else if (fieldValue is Dictionary<string, object?> nestedDict && fieldDefinition.Fields.Count > 0)
                {
                    ValidateData(nestedDict, fieldDefinition.Fields, result, $"{fieldPath}.", scope);
                }
                continue;
            }

            // Scalar field: run validation rules
            if (fieldDefinition.ValidationRule is null || fieldDefinition.ValidationRule.Validations is null)
            {
                continue;
            }

            var fieldType = fieldDefinition.Type;
            var elements = fieldDefinition.IsArray ? AsElements(fieldValue) : null;
            foreach (var stored in fieldDefinition.ValidationRule.Validations)
            {
                if (!stored.IsActive || WithVariablesResolved(stored, fieldPath, scope) is not { } validation)
                {
                    continue;
                }

                // Rules saved before EvaluationVersion existed (0) keep checking the list as a whole, as they always did.
                if (elements is null || validation.EvaluationVersion < 1)
                {
                    AddIfInvalid(result, ValidateField(fieldPath, fieldValue, fieldType, validation));
                }
                else
                {
                    ValidateElements(result, fieldPath, elements, fieldType, validation);
                }
            }
        }
    }

    /// <summary>
    /// For a version-1 rule, a list of plain values is checked element by element (path <c>Field[i]</c>),
    /// so e.g. a max length applies to each tag rather than to the list's text. Not empty means the list
    /// has at least one item; null elements are skipped.
    /// </summary>
    private static void ValidateElements(
        DataValidationResult result,
        string fieldPath,
        List<object?> elements,
        string fieldType,
        ValidationRule validation)
    {
        if (validation.Type == ValidationType.NotEmpty)
        {
            if (elements.Count == 0)
            {
                var message = string.IsNullOrWhiteSpace(validation.ErrorMessage)
                    ? $"Field '{fieldPath}' cannot be empty." : validation.ErrorMessage;
                result.Errors.Add(new ValidationError { FieldName = fieldPath, Message = message, ValidationType = nameof(ValidationType.NotEmpty) });
            }
            return;
        }

        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index] is not null)
            {
                AddIfInvalid(result, ValidateField($"{fieldPath}[{index}]", elements[index], fieldType, validation));
            }
        }
    }

    /// <summary>The elements of a list value, or null when the value is not a list (or is a string).</summary>
    internal static List<object?>? AsElements(object? value) =>
        value is System.Collections.IEnumerable list and not string ? list.Cast<object?>().ToList() : null;

    private static void AddIfInvalid(DataValidationResult result, ValidationError? error)
    {
        if (error is not null)
        {
            result.Errors.Add(error);
        }
    }

    /// <summary>
    /// Validates a single field against a validation rule
    /// </summary>
    /// <returns>ValidationError if validation fails, null otherwise</returns>
    private static ValidationError? ValidateField(
        string fieldName,
        object? fieldValue,
        string fieldType,
        ValidationRule validation)
    {
        var errorMessage = string.IsNullOrWhiteSpace(validation.ErrorMessage)
        ? $"Field '{fieldName}' is invalid." : validation.ErrorMessage;

        return validation.Type switch
        {
            ValidationType.NotEmpty => ValidateNotEmpty(fieldName, fieldValue, errorMessage),
            ValidationType.Regex => ValidateRegex(fieldName, fieldValue, validation.Value, errorMessage),
            ValidationType.MinLength => ValidateMinLength(fieldName, fieldValue, validation.Value, errorMessage),
            ValidationType.MaxLength => ValidateMaxLength(fieldName, fieldValue, validation.Value, errorMessage),
            ValidationType.LengthRange => ValidateLengthRange(fieldName, fieldValue, validation.Value, validation.SecondaryValue, errorMessage),
            ValidationType.Equal => ValidateEqual(fieldName, fieldValue, validation.Value, fieldType, errorMessage),
            ValidationType.NotEqual => ValidateNotEqual(fieldName, fieldValue, validation.Value, fieldType, errorMessage),
            ValidationType.GreaterThan => ValidateComparison(fieldName, fieldValue, validation.Value, fieldType, ComparisonType.GreaterThan, errorMessage),
            ValidationType.LessThan => ValidateComparison(fieldName, fieldValue, validation.Value, fieldType, ComparisonType.LessThan, errorMessage),
            ValidationType.GreaterThanOrEqual => ValidateComparison(fieldName, fieldValue, validation.Value, fieldType, ComparisonType.GreaterThanOrEqual, errorMessage),
            ValidationType.LessThanOrEqual => ValidateComparison(fieldName, fieldValue, validation.Value, fieldType, ComparisonType.LessThanOrEqual, errorMessage),
            ValidationType.Range => ValidateRange(fieldName, fieldValue, validation.Value, validation.SecondaryValue, fieldType, errorMessage),
            // Needs the target schema's data, so it runs as its own async step in MutationService.
            ValidationType.Reference => null,
            _ => null
        };
    }

    #region Validation Methods

    private static ValidationError? ValidateNotEmpty(string fieldName, object? fieldValue, string? errorMessage)
    {
        if (fieldValue is null || string.IsNullOrWhiteSpace(fieldValue.ToString()))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                Message = errorMessage ?? $"Field '{fieldName}' cannot be empty.",
                ValidationType = nameof(ValidationType.NotEmpty)
            };
        }
        return null;
    }

    private static ValidationError? ValidateRegex(string fieldName, object? fieldValue, object? pattern, string? errorMessage)
    {
        if (fieldValue is not null && pattern is not null)
        {
            var patternStr = pattern.ToString();
            if (!string.IsNullOrEmpty(patternStr) &&
                !Regex.IsMatch(fieldValue.ToString() ?? "", patternStr, RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                return new ValidationError
                {
                    FieldName = fieldName,
                    Message = errorMessage ?? $"Field '{fieldName}' does not match the required pattern.",
                    ValidationType = nameof(ValidationType.Regex)
                };
            }
        }
        return null;
    }

    private static ValidationError? ValidateMinLength(string fieldName, object? fieldValue, object? minLengthValue, string? errorMessage)
    {
        if (fieldValue is not null && minLengthValue is not null)
        {
            var minLength = Convert.ToInt32(minLengthValue);
            var strValue = fieldValue.ToString() ?? "";
            if (strValue.Length < minLength)
            {
                return new ValidationError
                {
                    FieldName = fieldName,
                    Message = errorMessage ?? $"Field '{fieldName}' must be at least {minLength} characters long.",
                    ValidationType = nameof(ValidationType.MinLength)
                };
            }
        }
        return null;
    }

    private static ValidationError? ValidateMaxLength(string fieldName, object? fieldValue, object? maxLengthValue, string? errorMessage)
    {
        if (fieldValue is not null && maxLengthValue is not null)
        {
            var maxLength = Convert.ToInt32(maxLengthValue);
            var strValue = fieldValue.ToString() ?? "";
            if (strValue.Length > maxLength)
            {
                return new ValidationError
                {
                    FieldName = fieldName,
                    Message = errorMessage ?? $"Field '{fieldName}' must not exceed {maxLength} characters.",
                    ValidationType = nameof(ValidationType.MaxLength)
                };
            }
        }
        return null;
    }

    private static ValidationError? ValidateLengthRange(string fieldName, object? fieldValue, object? minValue, object? maxValue, string? errorMessage)
    {
        if (fieldValue is not null && minValue is not null && maxValue is not null)
        {
            var minLen = Convert.ToInt32(minValue);
            var maxLen = Convert.ToInt32(maxValue);
            var strValue = fieldValue.ToString() ?? "";
            if (strValue.Length < minLen || strValue.Length > maxLen)
            {
                return new ValidationError
                {
                    FieldName = fieldName,
                    Message = errorMessage ?? $"Field '{fieldName}' must be between {minLen} and {maxLen} characters.",
                    ValidationType = nameof(ValidationType.LengthRange)
                };
            }
        }
        return null;
    }

    private static ValidationError? ValidateEqual(string fieldName, object? fieldValue, object? validationValue, string fieldType, string? errorMessage)
    {
        if (!IsEqual(fieldValue, validationValue, fieldType))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                Message = errorMessage ?? $"Field '{fieldName}' must be equal to {validationValue}.",
                ValidationType = nameof(ValidationType.Equal)
            };
        }
        return null;
    }

    private static ValidationError? ValidateNotEqual(string fieldName, object? fieldValue, object? validationValue, string fieldType, string? errorMessage)
    {
        if (IsEqual(fieldValue, validationValue, fieldType))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                Message = errorMessage ?? $"Field '{fieldName}' must not be equal to {validationValue}.",
                ValidationType = nameof(ValidationType.NotEqual)
            };
        }
        return null;
    }

    private static ValidationError? ValidateComparison(string fieldName, object? fieldValue, object? validationValue, string fieldType, ComparisonType comparisonType, string? errorMessage)
    {
        if (!CompareValues(fieldValue, validationValue, fieldType, comparisonType))
        {
            var comparisonText = comparisonType switch
            {
                ComparisonType.GreaterThan => "greater than",
                ComparisonType.LessThan => "less than",
                ComparisonType.GreaterThanOrEqual => "greater than or equal to",
                ComparisonType.LessThanOrEqual => "less than or equal to",
                _ => "compared to"
            };

            return new ValidationError
            {
                FieldName = fieldName,
                Message = errorMessage ?? $"Field '{fieldName}' must be {comparisonText} {validationValue}.",
                ValidationType = comparisonType.ToString()
            };
        }
        return null;
    }

    private static ValidationError? ValidateRange(string fieldName, object? fieldValue, object? minValue, object? maxValue, string fieldType, string? errorMessage)
    {
        if (!IsInRange(fieldValue, minValue, maxValue, fieldType))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                Message = errorMessage ?? $"Field '{fieldName}' must be between {minValue} and {maxValue}.",
                ValidationType = nameof(ValidationType.Range)
            };
        }
        return null;
    }

    #endregion

    #region Comparison Helper Methods

    /// <summary>
    /// Checks if two values are equal based on the field type
    /// </summary>
    private static bool IsEqual(object? fieldValue, object? validationValue, string fieldType)
    {
        if (fieldValue is null && validationValue is null) return true;
        if (fieldValue is null || validationValue is null) return false;

        try
        {
            return fieldType.ToLowerInvariant() switch
            {
				"int" or "long" => Convert.ToInt64(fieldValue) == Convert.ToInt64(validationValue),
				"float" or "double" or "decimal" => Math.Abs(Convert.ToDouble(fieldValue) - Convert.ToDouble(validationValue)) < 0.0001,
				"datetime" or "date" => Convert.ToDateTime(fieldValue) == Convert.ToDateTime(validationValue),
				"boolean" or "bool" => Convert.ToBoolean(fieldValue) == Convert.ToBoolean(validationValue),
                _ => fieldValue.ToString() == validationValue.ToString()
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compares two values based on the field type and comparison type
    /// </summary>
    private static bool CompareValues(object? fieldValue, object? validationValue, string fieldType, ComparisonType comparisonType)
    {
        if (fieldValue is null || validationValue is null) return false;

        try
        {
            int comparison = fieldType.ToLowerInvariant() switch
            {
				"int" or "long" => Convert.ToInt64(fieldValue).CompareTo(Convert.ToInt64(validationValue)),
				"float" or "double" or "decimal" => Convert.ToDouble(fieldValue).CompareTo(Convert.ToDouble(validationValue)),
				"datetime" or "date" => Convert.ToDateTime(fieldValue).CompareTo(Convert.ToDateTime(validationValue)),
                _ => string.Compare(fieldValue.ToString(), validationValue.ToString(), StringComparison.Ordinal)
            };

            return comparisonType switch
            {
                ComparisonType.GreaterThan => comparison > 0,
                ComparisonType.LessThan => comparison < 0,
                ComparisonType.GreaterThanOrEqual => comparison >= 0,
                ComparisonType.LessThanOrEqual => comparison <= 0,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a value is within a specified range
    /// </summary>
    private static bool IsInRange(object? fieldValue, object? minValue, object? maxValue, string fieldType)
    {
        if (fieldValue is null || minValue is null || maxValue is null) return false;

        return CompareValues(fieldValue, minValue, fieldType, ComparisonType.GreaterThanOrEqual)
            && CompareValues(fieldValue, maxValue, fieldType, ComparisonType.LessThanOrEqual);
    }

    #endregion
}
