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
    /// <summary>The error when a rule's "only when" condition could not be evaluated.</summary>
    public const string ConditionNotChecked = "This rule's condition couldn't be checked. Ask the project's admin to review the rule.";

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
    public static DataValidationResult Validate(this Dictionary<string, object?> input, SchemaDefinitionExtended schema, IReadOnlyDictionary<string, object?>? storedRecord) =>
        input.Validate(schema, storedRecord, DateTime.UtcNow);

    /// <summary>
    /// As <see cref="Validate(Dictionary{string, object?}, SchemaDefinitionExtended, IReadOnlyDictionary{string, object?}?)"/>, with
    /// the moment a date relative to now (<c>{{Now-18y}}</c>) is read from, in UTC; callers pass it once per request.
    /// <paramref name="rawInput"/> is the input as sent, explicit nulls included, for a caller whose
    /// <paramref name="input"/> has lost them (<c>WriteRules.ToRecord</c>): "only when" rules read it to know what was sent
    /// and cleared. Rules without a condition never read it.
    /// </summary>
    public static DataValidationResult Validate(this Dictionary<string, object?> input, SchemaDefinitionExtended schema, IReadOnlyDictionary<string, object?>? storedRecord, DateTime nowUtc, MongoDB.Bson.BsonDocument? rawInput = null)
    {
        var result = new DataValidationResult();
        var scope = new VariableScope(input, storedRecord, nowUtc);
        ValidateData(input, schema.Fields, result, pathPrefix: "", scope);
        ValidateConditional(input, schema, storedRecord, rawInput, scope, result);
        return result;
    }

    /// <summary>
    /// The rules without an "only when" condition, on the input alone: what <see cref="Validate(Dictionary{string, object?}, SchemaDefinitionExtended)"/>
    /// checks besides the conditional rules. Bulk update runs this once for the input, as before, and
    /// <see cref="ValidateConditions"/> once per matched record.
    /// </summary>
    public static DataValidationResult ValidateWithoutConditions(this Dictionary<string, object?> input, SchemaDefinitionExtended schema, DateTime nowUtc)
    {
        var result = new DataValidationResult();
        ValidateData(input, schema.Fields, result, pathPrefix: "", new VariableScope(input, null, nowUtc));
        return result;
    }

    /// <summary>
    /// Only the rules with an "only when" condition, on <paramref name="storedRecord"/> with the input on top, as an update
    /// of that record. Bulk update runs this once per matched record.
    /// </summary>
    public static DataValidationResult ValidateConditions(this Dictionary<string, object?> input, SchemaDefinitionExtended schema, IReadOnlyDictionary<string, object?> storedRecord, DateTime nowUtc, MongoDB.Bson.BsonDocument? rawInput = null)
    {
        var result = new DataValidationResult();
        ValidateConditional(input, schema, storedRecord, rawInput, new VariableScope(input, storedRecord, nowUtc), result);
        return result;
    }

    /// <summary>The whole record a variable reads from (the input as root, then the stored record), and the moment "now" means.</summary>
    private sealed record VariableScope(IReadOnlyDictionary<string, object?> Root, IReadOnlyDictionary<string, object?>? Stored, DateTime NowUtc);

    private static readonly HashSet<ValidationType> VariableRuleTypes =
    [
        ValidationType.Equal, ValidationType.NotEqual, ValidationType.GreaterThan, ValidationType.LessThan,
        ValidationType.GreaterThanOrEqual, ValidationType.LessThanOrEqual, ValidationType.Range,
    ];

    /// <summary>
    /// The rule with any variable values replaced by what they read, and any date relative to now
    /// (<c>{{Now-18y}}</c>) by the moment it means; null when a variable has no value, so the rule is skipped.
    /// Only version-1 rules have either: an older rule's text stays text.
    /// </summary>
    private static ValidationRule? WithVariablesResolved(ValidationRule rule, string fieldPath, VariableScope scope)
    {
        if (rule.EvaluationVersion < 1 || !VariableRuleTypes.Contains(rule.Type))
            return rule;

        var hasValueVariable = VariableSyntax.TryGetSingleVariable(rule.Value, out var valueVariable);
        var hasSecondaryVariable = VariableSyntax.TryGetSingleVariable(rule.SecondaryValue, out var secondaryVariable);
        var valueIsNow = RelativeNow.TryParse(rule.Value, out var valueNow);
        var secondaryIsNow = RelativeNow.TryParse(rule.SecondaryValue, out var secondaryNow);
        if (!hasValueVariable && !hasSecondaryVariable && !valueIsNow && !secondaryIsNow)
            return rule;

        var value = hasValueVariable ? VariableSyntax.Resolve(valueVariable, scope.Root, scope.Stored, null)
            : valueIsNow ? valueNow.From(scope.NowUtc) : rule.Value;
        var secondary = hasSecondaryVariable ? VariableSyntax.Resolve(secondaryVariable, scope.Root, scope.Stored, null)
            : secondaryIsNow ? secondaryNow.From(scope.NowUtc) : rule.SecondaryValue;
        if (value is null || (rule.Type == ValidationType.Range && secondary is null))
            return null;

        string Show(bool isVariable, VariablePart variable, bool isNow, RelativeNow now, object? resolved) =>
            isVariable ? $"{variable.Name} ({Display(resolved)})"
            : isNow ? $"{now.Describe()} ({ShowMoment(now, (DateTime)resolved!)})"
            : Display(resolved);

        var message = rule.ErrorMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            var bound = Show(hasValueVariable, valueVariable, valueIsNow, valueNow, value);
            // A bound relative to now reads as a date: "on or before now − 18 years (2008-10-25)".
            var dates = valueIsNow || secondaryIsNow;
            message = rule.Type switch
            {
                ValidationType.Equal => $"{fieldPath} must be equal to {bound}",
                ValidationType.NotEqual => $"{fieldPath} must be different from {bound}",
                ValidationType.GreaterThan => $"{fieldPath} must be {(dates ? "after" : "greater than")} {bound}",
                ValidationType.LessThan => $"{fieldPath} must be {(dates ? "before" : "less than")} {bound}",
                ValidationType.GreaterThanOrEqual => $"{fieldPath} must be {(dates ? "on or after" : "greater than or equal to")} {bound}",
                ValidationType.LessThanOrEqual => $"{fieldPath} must be {(dates ? "on or before" : "less than or equal to")} {bound}",
                _ => $"{fieldPath} must be between {bound} and {Show(hasSecondaryVariable, secondaryVariable, secondaryIsNow, secondaryNow, secondary)}",
            };
        }

        return WithValues(rule, value, secondary, message);
    }

    /// <summary>The moment a date relative to now stands for: the date alone for years, months and days; with the time otherwise.</summary>
    private static string ShowMoment(RelativeNow now, DateTime moment) =>
        now.ShowsTime
            ? moment.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC"
            : moment.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A copy of <paramref name="rule"/> with these values and message; every other part (the "only when"
    /// condition, the reference filter, …) is kept, so resolving a variable never changes what the rule checks.
    /// </summary>
    internal static ValidationRule WithValues(ValidationRule rule, object? value, object? secondary, string? message) => new()
    {
        Type = rule.Type,
        Value = value,
        SecondaryValue = secondary,
        ErrorMessage = message,
        IsActive = rule.IsActive,
        EvaluationVersion = rule.EvaluationVersion,
        When = rule.When,
        TargetFilter = rule.TargetFilter,
    };

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

                // A list of objects can be counted, as a whole (MinItems, MaxItems): no other rule reads its value.
                if (fieldDefinition.IsArray && AsElements(fieldValue) is { } objects)
                {
                    foreach (var rule in fieldDefinition.ValidationRule?.Validations ?? [])
                    {
                        if (rule.IsActive && rule.When is null && rule.Type is ValidationType.MinItems or ValidationType.MaxItems)
                            AddIfInvalid(result, ValidateWholeList(fieldPath, objects, fieldDefinition.Type, rule));
                    }
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
                // A rule with an "only when" condition runs in the conditional pass, on the merged record.
                if (!stored.IsActive || stored.When is not null || WithVariablesResolved(stored, fieldPath, scope) is not { } validation)
                {
                    continue;
                }

                ApplyRule(result, fieldPath, fieldValue, elements, fieldType, validation);
            }
        }
    }

    /// <summary>One rule on one property's value: the list as a whole, each item, or the single value.</summary>
    private static void ApplyRule(DataValidationResult result, string fieldPath, object? fieldValue, List<object?>? elements, string fieldType, ValidationRule validation)
    {
        // MinItems, MaxItems and UniqueItems check the whole list, whatever the version (R2).
        if (WholeListTypes.Contains(validation.Type))
        {
            if (elements is not null)
                AddIfInvalid(result, ValidateWholeList(fieldPath, elements, fieldType, validation));
            return;
        }

        // Rules saved before EvaluationVersion existed (0) keep checking the list as a whole, as they always did.
        // In and NotIn are newer and never read the version: they always check each item (R2).
        if (elements is null || (validation.EvaluationVersion < 1 && !PerItemTypes.Contains(validation.Type)))
        {
            AddIfInvalid(result, ValidateField(fieldPath, fieldValue, fieldType, validation));
        }
        else
        {
            ValidateElements(result, fieldPath, elements, fieldType, validation);
        }
    }

    /// <summary>
    /// Rules with an "only when" condition, on the merged record (validation and access rules plan, section 1). On an
    /// update a rule runs only when its own property or a property its condition uses was sent, so an update that
    /// touches neither never fails because of older data. "Not empty" checks the merged value even when the property was
    /// not sent ("required when"); other types skip a missing value. Costs nothing on a schema without conditions.
    /// </summary>
    private static void ValidateConditional(
        Dictionary<string, object?> input,
        SchemaDefinitionExtended schema,
        IReadOnlyDictionary<string, object?>? storedRecord,
        MongoDB.Bson.BsonDocument? rawInput,
        VariableScope scope,
        DataValidationResult result)
    {
        var conditional = ConditionalRules(schema.Fields, pathPrefix: "").ToList();
        if (conditional.Count == 0)
            return;

        var merged = ValidationRecord.Build(input, rawInput, storedRecord, schema.Fields);
        var sent = rawInput is null ? input.Keys.ToHashSet(StringComparer.Ordinal) : rawInput.Names.Concat(input.Keys).ToHashSet(StringComparer.Ordinal);
        var isUpdate = storedRecord is not null;
        foreach (var (fieldPath, field, rule) in conditional)
        {
            if (isUpdate && !Touches(sent, fieldPath, rule.When!))
                continue;

            switch (ValidationCondition.Evaluate(rule.When, merged))
            {
                case ConditionOutcome.DoesNotHold:
                    continue;
                case ConditionOutcome.CannotCheck:
                    result.Errors.Add(new ValidationError { FieldName = fieldPath, Message = ConditionNotChecked, ValidationType = rule.Type.ToString() });
                    continue;
            }

            var value = ValidationRecord.Read(merged, fieldPath);
            if (value is null && rule.Type != ValidationType.NotEmpty)
                continue;
            if (WithVariablesResolved(rule, fieldPath, scope) is not { } validation)
                continue;

            ApplyRule(result, fieldPath, value, field.IsArray ? AsElements(value) : null, field.Type, validation);
        }
    }

    /// <summary>
    /// Every active rule with a condition, on single-value properties and inside nested objects (never inside a list of
    /// objects), and the item counts of a list of objects.
    /// </summary>
    private static IEnumerable<(string Path, FieldDefinitionResponse Field, ValidationRule Rule)> ConditionalRules(List<FieldDefinitionResponse> fields, string pathPrefix)
    {
        foreach (var field in fields)
        {
            var path = $"{pathPrefix}{field.Name}";
            if (!SchemaTypeHelper.IsScalar(field.Type))
            {
                if (!field.IsArray)
                {
                    foreach (var nested in ConditionalRules(field.Fields, $"{path}."))
                        yield return nested;
                }
                else
                {
                    foreach (var rule in field.ValidationRule?.Validations ?? [])
                    {
                        if (rule.IsActive && rule.When is not null && rule.Type is ValidationType.MinItems or ValidationType.MaxItems)
                            yield return (path, field, rule);
                    }
                }
                continue;
            }

            foreach (var rule in field.ValidationRule?.Validations ?? [])
            {
                if (rule.IsActive && rule.When is not null)
                    yield return (path, field, rule);
            }
        }
    }

    /// <summary>True when the input sends the rule's property or a property its condition uses.</summary>
    private static bool Touches(HashSet<string> sent, string fieldPath, PolicyRuleGroup when)
    {
        static string Top(string path) => path.Split('.')[0];

        static IEnumerable<string> Operands(PolicyRuleGroup group) =>
            group.Rules.SelectMany(rule => new[]
            {
                rule.LeftSource == ConditionSource.SCHEMA_FIELD ? rule.LeftOperand : null,
                rule.RightSource == ConditionSource.SCHEMA_FIELD ? rule.RightOperand : null,
            }).OfType<string>().Concat(group.NestedGroups.SelectMany(Operands));

        return sent.Contains(Top(fieldPath)) || Operands(when).Any(operand => sent.Contains(Top(operand)));
    }

    private static readonly HashSet<ValidationType> WholeListTypes = [ValidationType.MinItems, ValidationType.MaxItems, ValidationType.UniqueItems];

    private static readonly HashSet<ValidationType> PerItemTypes = [ValidationType.In, ValidationType.NotIn];

    /// <summary>MinItems, MaxItems, UniqueItems: the list as a whole. Items compare as Equal compares them (numbers as numbers, text exactly).</summary>
    private static ValidationError? ValidateWholeList(string fieldPath, List<object?> elements, string fieldType, ValidationRule validation)
    {
        string? problem;
        switch (validation.Type)
        {
            case ValidationType.MinItems:
                problem = Count(validation.Value) is { } min && elements.Count < min ? $"Field '{fieldPath}' must have at least {min} items." : null;
                break;
            case ValidationType.MaxItems:
                problem = Count(validation.Value) is { } max && elements.Count > max ? $"Field '{fieldPath}' must have at most {max} items." : null;
                break;
            default:
                var repeated = FirstRepeated(elements, fieldType);
                problem = repeated is null ? null : $"Field '{fieldPath}' lists {Display(repeated)} twice.";
                break;
        }

        return problem is null ? null : new ValidationError
        {
            FieldName = fieldPath,
            Message = string.IsNullOrWhiteSpace(validation.ErrorMessage) ? problem : validation.ErrorMessage,
            ValidationType = validation.Type.ToString(),
        };
    }

    /// <summary>
    /// The first item that repeats an earlier one, compared as Equal compares them (numbers as numbers, text exactly),
    /// in one pass: a long list must not cost a write quadratic time.
    /// </summary>
    private static object? FirstRepeated(List<object?> elements, string fieldType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in elements)
        {
            if (item is not null && EqualityKey(item, fieldType) is { } key && !seen.Add(key))
                return item;
        }
        return null;
    }

    /// <summary>The key two items share when IsEqual holds for them; null for an item that never equals another.</summary>
    private static string? EqualityKey(object item, string fieldType)
    {
        try
        {
            return fieldType.ToLowerInvariant() switch
            {
                "int" or "long" => Convert.ToInt64(item, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
                // IsEqual allows 0.0001 between floats; rounding to 4 places groups the same values.
                "float" or "double" or "decimal" => Math.Round(Convert.ToDouble(item, System.Globalization.CultureInfo.InvariantCulture), 4).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "datetime" or "date" => Convert.ToDateTime(item, System.Globalization.CultureInfo.InvariantCulture).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "boolean" or "bool" => Convert.ToBoolean(item, System.Globalization.CultureInfo.InvariantCulture) ? "true" : "false",
                _ => item.ToString(),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static long? Count(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
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
            ValidationType.In => ValidateIn(fieldName, fieldValue, validation, fieldType, mustBeListed: true),
            ValidationType.NotIn => ValidateIn(fieldName, fieldValue, validation, fieldType, mustBeListed: false),
            // Need the target schema's data, so they run as their own async step (MutationService, ResolveWriteAsync).
            ValidationType.Reference or ValidationType.NotExistsIn => null,
            _ => null
        };
    }

    #region Validation Methods

    /// <summary>In / NotIn: the value against the rule's list, each compared as Equal compares it (text exactly, numbers as numbers).</summary>
    private static ValidationError? ValidateIn(string fieldName, object? fieldValue, ValidationRule validation, string fieldType, bool mustBeListed)
    {
        if (fieldValue is null)
            return null;
        // Without a list the rule can't say which values are allowed: refused, never let through.
        if (validation.Value is string || AsElements(validation.Value) is not { } listed)
            return new ValidationError { FieldName = fieldName, Message = ValidationRuleCheck.ValuesMustBeAList, ValidationType = validation.Type.ToString() };
        if (listed.Any(item => IsEqual(fieldValue, item, fieldType)) == mustBeListed)
            return null;

        var values = string.Join(", ", listed.Select(Display));
        return new ValidationError
        {
            FieldName = fieldName,
            Message = string.IsNullOrWhiteSpace(validation.ErrorMessage)
                ? $"Field '{fieldName}' must {(mustBeListed ? "" : "not ")}be one of: {values}."
                : validation.ErrorMessage,
            ValidationType = validation.Type.ToString(),
        };
    }

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
