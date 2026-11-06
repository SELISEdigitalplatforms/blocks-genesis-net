using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// Checks a validation rule's variable values before the rule is saved (Data Gateway API, OS Data Access
/// API, schema import): one <c>{{Schema.X}}</c> as the whole value, only in comparison rules, naming an
/// existing single-value property of the same kind as the property being checked.
/// </summary>
public static class ValidationVariableCheck
{
    private static readonly HashSet<ValidationType> ComparisonTypes =
    [
        ValidationType.Equal, ValidationType.NotEqual, ValidationType.GreaterThan, ValidationType.LessThan,
        ValidationType.GreaterThanOrEqual, ValidationType.LessThanOrEqual, ValidationType.Range,
    ];

    /// <summary>The first problem as a user-facing message, or null. Rules saved before variables existed (version 0) are never read as variables.</summary>
    public static string? Check(ValidationRule rule, string fieldType, string schemaName, IReadOnlyDictionary<string, VariableField> fields)
    {
        if (rule.EvaluationVersion < 1)
            return null;

        foreach (var raw in new[] { rule.Value, rule.SecondaryValue })
        {
            // Only text that looks like a variable is parsed, so fixed values with braces stay fixed values.
            if (raw is not string text || !(text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal)))
                continue;
            // A date relative to now ({{Now-18y}}) is not a variable; ValidationRuleCheck checks it.
            if (RelativeNow.IsWritten(text))
                continue;

            var (parts, error) = VariableSyntax.Parse(text, VariableMode.SingleVariable, schemaName);
            error ??= VariableSyntax.CheckReferences(parts, VariableMode.SingleVariable, schemaName, fields, targetIsList: false);
            if (error is not null)
                return error.Message;

            if (parts.FirstOrDefault(p => p.IsVariable) is not { } variable)
                continue;
            if (!ComparisonTypes.Contains(rule.Type))
                return $"Only comparison rules (Equal, Not equal, greater/less than, Range) can use a variable at position {variable.Position}";

            var target = fields[variable.Path!];
            if (Kind(target.Type) != Kind(fieldType) || target.IsArray)
                return $"{variable.Name} is {Describe(target.Type, target.IsArray)} at position {variable.Position}: {Describe(fieldType, false)} is expected";
        }
        return null;
    }

    private static string Kind(string type) => type switch
    {
        "Int" or "Float" => "number",
        "DateTime" => "date",
        "Boolean" => "boolean",
        _ => "text",
    };

    private static string Describe(string type, bool isArray) => isArray ? "a list" : Kind(type) switch
    {
        "number" => "a number",
        "date" => "a date-time",
        "boolean" => "true or false",
        _ => "text",
    };
}
