using System.Collections;
using System.Globalization;
using System.Text.Json;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>The property a validation rule is on: its full dotted path, type, whether it is a list, and an Enum's values.</summary>
public sealed record RuleField(string Path, string Type, bool IsArray, IReadOnlyList<string>? EnumValues = null);

/// <summary>
/// Checks the new validation features before a rule is saved (Data Gateway API, schema import, publish, and the
/// OS platform rules page): the types In, NotIn, MinItems, MaxItems, UniqueItems and NotExistsIn, the "only when"
/// condition (<see cref="ValidationRule.When"/>) and the reference filter (<see cref="ValidationRule.TargetFilter"/>).
/// A rule that uses none of them is never refused here, so every rule saved before them keeps saving as it does.
/// </summary>
public static class ValidationRuleCheck
{
    public const string NotOnSystemSchemas =
        "This rule can't be used on a system schema: system schemas take only the older rule types, with no condition, filter or date relative to now";
    public const string ValuesMustBeAList = "Is one of / is not one of needs a list of values";
    public const string ValuesMustNotBeEmpty = "Is one of / is not one of needs at least one value";
    public const int MaxListValues = 500;
    public const string TooManyValues = "Is one of / is not one of takes at most 500 values";
    public const string OnlyForLists = "This rule checks a list, so it only works on a list property";
    public const string CountMustBeWholeNumber = "Enter a whole number of items, 0 or more";
    public const string NotExistsInOwnSchema = "A property can't be checked against its own schema: mark it as unique instead";
    public const string TargetSchemaRequired = "Choose the schema to check against";

    public const string ConditionLeftMustBeProperty = "A condition must start with a property of the record";
    public const string AuthNotAllowed = "Auth values aren't available in validation rules: use an access policy";
    public const string PlaceholderNotAllowed = "A fixed value can't contain {{…}}: compare with a property instead";
    public const string FieldComparisonOperator = "Two properties can only be compared with equals, is not, greater than or less than";
    public const string FieldComparisonItemId = "ItemId can't be compared with another property";
    public const string CommaInSingleValue = "A single value can't contain a comma: use \"is one of\" for a list";
    public const string ConditionNeedsValue = "Enter a value to compare with";
    public const string ConditionInsideList = "A rule on a property inside a list of objects can't have a condition yet";
    public const string FilterOnlyForReferences = "Only \"exists in\" and \"must not exist in\" rules take a filter";
    public const string FilterMustUseFixedValues = "A filter compares the other schema's properties with fixed values only";
    public const string ConditionOnLookup = "\"Exists in\" and \"must not exist in\" rules can't have a condition yet: use a filter on the other schema";
    public const string ConditionGroupEmpty = "Each group of conditions needs at least one condition";
    public const string UniqueItemsNeedsValues = "No duplicates works on a list of values, not a list of objects";

    public const string NowOnlyForDates = "A date relative to now only works on a date-time property";
    public const string NowOnlyForComparisons = "A date relative to now only works in greater than, less than and range rules";

    public static string InvalidNow(string value) => $"\"{value}\" isn't a date relative to now: write {RelativeNow.Shapes}";
    public static string UnknownProperty(string name) => $"{name} isn't a property of this schema";
    public static string FieldComparisonKind(string left, string right) => $"{left} and {right} hold different kinds of value and can't be compared";
    public static string InvalidPattern(string pattern) => $"\"{pattern}\" isn't a valid regular expression";

    public static string ValuesNotForType(string kind) => $"Is one of / is not one of can't be used on a {kind} property";
    public static string ValueDoesNotFit(string value, string expected) => $"\"{value}\" isn't a valid value here (expected: {expected})";
    public static string DuplicateValue(string value) => $"\"{value}\" is listed twice";

    private static readonly HashSet<ValidationType> NewTypes =
    [
        ValidationType.In, ValidationType.NotIn, ValidationType.MinItems, ValidationType.MaxItems, ValidationType.UniqueItems, ValidationType.NotExistsIn,
    ];

    /// <summary>One user-facing message per problem; empty when the rule may be saved.</summary>
    public static IReadOnlyList<string> Check(ValidationRule rule, RuleField field, string schemaName, bool systemSchema, IReadOnlyDictionary<string, VariableField> fields)
    {
        var problems = new List<string>();
        // Like variables, a date relative to now is read only from version 1: an older rule's text stays text.
        var usesNow = rule.EvaluationVersion >= 1 && (RelativeNow.IsWritten(rule.Value) || RelativeNow.IsWritten(rule.SecondaryValue));
        var usesNewFeatures = NewTypes.Contains(rule.Type) || rule.When is not null || rule.TargetFilter is not null || usesNow;
        if (!usesNewFeatures)
            return problems;

        // System schemas are read by the platform services, which may run an older Genesis.
        if (systemSchema)
            problems.Add(NotOnSystemSchemas);

        switch (rule.Type)
        {
            case ValidationType.In or ValidationType.NotIn:
                CheckValueList(rule.Value, field, problems);
                break;
            case ValidationType.MinItems or ValidationType.MaxItems:
                if (!field.IsArray) problems.Add(OnlyForLists);
                else if (WholeNumber(rule.Value) is not >= 0) problems.Add(CountMustBeWholeNumber);
                break;
            case ValidationType.UniqueItems:
                if (!field.IsArray) problems.Add(OnlyForLists);
                else if (!SchemaTypeHelper.IsScalar(field.Type)) problems.Add(UniqueItemsNeedsValues);
                break;
            case ValidationType.NotExistsIn:
                var target = (rule.Value as string ?? (rule.Value is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : null))?.Trim();
                if (string.IsNullOrEmpty(target)) problems.Add(TargetSchemaRequired);
                else if (string.Equals(target, schemaName, StringComparison.OrdinalIgnoreCase)) problems.Add(NotExistsInOwnSchema);
                break;
        }

        if (usesNow)
            CheckNow(rule, field, problems);

        if (rule.When is not null)
        {
            // The lookup runs whatever the record holds; a condition on it would be ignored.
            if (rule.Type is ValidationType.Reference or ValidationType.NotExistsIn) problems.Add(ConditionOnLookup);
            else if (IsInsideListOfObjects(field.Path, fields)) problems.Add(ConditionInsideList);
            else CheckGroup(rule.When, ConditionKind.When, fields, problems);
        }

        if (rule.TargetFilter is not null)
        {
            if (rule.Type is not (ValidationType.Reference or ValidationType.NotExistsIn)) problems.Add(FilterOnlyForReferences);
            else CheckGroup(rule.TargetFilter, ConditionKind.TargetFilter, fields, problems);
        }

        return problems.Distinct().ToList();
    }

    private static readonly HashSet<ValidationType> OrderingTypes =
    [
        ValidationType.GreaterThan, ValidationType.LessThan, ValidationType.GreaterThanOrEqual, ValidationType.LessThanOrEqual, ValidationType.Range,
    ];

    private static void CheckNow(ValidationRule rule, RuleField field, List<string> problems)
    {
        var malformed = new[] { rule.Value, rule.SecondaryValue }
            .Where(value => RelativeNow.IsWritten(value) && !RelativeNow.TryParse(value, out _))
            .Select(value => InvalidNow((string)value!))
            .ToList();
        problems.AddRange(malformed);
        if (malformed.Count > 0)
            return;

        if (field.Type != "DateTime")
            problems.Add(NowOnlyForDates);
        // Equal and Not equal would never pass: now moves on every save.
        else if (!OrderingTypes.Contains(rule.Type))
            problems.Add(NowOnlyForComparisons);
    }

    private enum ConditionKind { When, TargetFilter }

    // Comparing two properties: the field-to-field builder turns every other operator into $eq.
    private static readonly HashSet<PolicyOperator> FieldComparisonOperators =
    [
        PolicyOperator.EQUAL, PolicyOperator.NOT_EQUAL, PolicyOperator.GREATER_THAN, PolicyOperator.GREATER_THAN_OR_EQUAL,
        PolicyOperator.LESS_THAN, PolicyOperator.LESS_THAN_OR_EQUAL,
    ];

    /// <summary>
    /// Every condition in the group and its nested groups. Each refusal stops a combination the policy filter builder
    /// would quietly turn into something else (validation and access rules plan, section 1).
    /// </summary>
    private static void CheckGroup(PolicyRuleGroup group, ConditionKind kind, IReadOnlyDictionary<string, VariableField> fields, List<string> problems)
    {
        // An empty group is "not granted" inside another group, which would turn the whole condition off.
        if (group.Rules.Count == 0 && group.NestedGroups.Count == 0)
            problems.Add(ConditionGroupEmpty);
        foreach (var condition in group.Rules)
            CheckCondition(condition, kind, fields, problems);
        foreach (var nested in group.NestedGroups)
            CheckGroup(nested, kind, fields, problems);
    }

    private static void CheckCondition(PolicyRule condition, ConditionKind kind, IReadOnlyDictionary<string, VariableField> fields, List<string> problems)
    {
        // A fixed value or an Auth value on the left reaches the builder's catch-all, a full-access grant.
        if (condition.LeftSource != ConditionSource.SCHEMA_FIELD)
        {
            problems.Add(ConditionLeftMustBeProperty);
            return;
        }

        if (kind == ConditionKind.TargetFilter)
        {
            // The left side is a property of the target schema, which the caller checks against that schema.
            if (condition.RightSource != ConditionSource.STATIC_VALUE) problems.Add(FilterMustUseFixedValues);
            else CheckFixedValue(condition, problems);
            return;
        }

        switch (condition.RightSource)
        {
            case ConditionSource.AUTH:
                problems.Add(AuthNotAllowed);
                break;
            case ConditionSource.SCHEMA_FIELD:
                CheckFieldComparison(condition, fields, problems);
                break;
            default:
                if (!IsRecordProperty(condition.LeftOperand, fields)) problems.Add(UnknownProperty(condition.LeftOperand));
                else CheckFixedValue(condition, problems);
                break;
        }
    }

    private static void CheckFieldComparison(PolicyRule condition, IReadOnlyDictionary<string, VariableField> fields, List<string> problems)
    {
        // Inside $expr, "$ItemId" is never mapped to _id.
        if (IsItemId(condition.LeftOperand) || IsItemId(condition.RightOperand))
            problems.Add(FieldComparisonItemId);
        else if (!FieldComparisonOperators.Contains(condition.Operator))
            problems.Add(FieldComparisonOperator);
        else if (!fields.TryGetValue(condition.LeftOperand, out var left))
            problems.Add(UnknownProperty(condition.LeftOperand));
        else if (!fields.TryGetValue(condition.RightOperand, out var right))
            problems.Add(UnknownProperty(condition.RightOperand));
        else if (ValueKind(left.Type) != ValueKind(right.Type) || left.IsArray != right.IsArray)
            problems.Add(FieldComparisonKind(condition.LeftOperand, condition.RightOperand));
    }

    private static void CheckFixedValue(PolicyRule condition, List<string> problems)
    {
        if (condition.Operator is PolicyOperator.IS_NULL or PolicyOperator.IS_NOT_NULL)
            return;

        var texts = condition.StaticValue switch
        {
            string text => [text],
            null => condition.RightOperands,
            _ => AsList(condition.StaticValue)?.OfType<string>().ToList() ?? [],
        };
        if (condition.StaticValue is null && texts.Count == 0 || condition.StaticValue is string blank && string.IsNullOrWhiteSpace(blank))
        {
            problems.Add(ConditionNeedsValue);
            return;
        }

        // {{Auth.X}}, {{token.X}} and {{Schema.X}} all stay placeholders here: refused, never read as text.
        if (texts.Any(text => text.Contains("{{", StringComparison.Ordinal)))
        {
            problems.Add(PlaceholderNotAllowed);
            return;
        }

        if (condition.StaticValue is string single)
        {
            // The builder splits a comma-separated value into a list.
            if (condition.Operator is not (PolicyOperator.IN or PolicyOperator.NOT_IN) && single.Contains(','))
                problems.Add(CommaInSingleValue);
            else if (condition.Operator == PolicyOperator.REGEX && !IsPattern(single))
                problems.Add(InvalidPattern(single));
        }
    }

    private static bool IsPattern(string pattern)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsItemId(string? name) => string.Equals(name, ReferenceTargets.DefaultTargetField, StringComparison.Ordinal);

    private static bool IsRecordProperty(string name, IReadOnlyDictionary<string, VariableField> fields) => IsItemId(name) || fields.ContainsKey(name);

    /// <summary>True when a parent of the property is a list of objects ("Items.Qty" inside "Items").</summary>
    private static bool IsInsideListOfObjects(string path, IReadOnlyDictionary<string, VariableField> fields)
    {
        var segments = path.Split('.');
        for (var length = 1; length < segments.Length; length++)
        {
            if (fields.TryGetValue(string.Join('.', segments[..length]), out var parent) && parent.IsArray && parent.IsObject)
                return true;
        }
        return false;
    }

    private static string ValueKind(string type) => type switch
    {
        "Int" or "Float" => "number",
        "DateTime" => "date",
        "Boolean" => "boolean",
        _ => "text",
    };

    /// <summary>In / NotIn: a non-empty list of at most 500 distinct values, each of the property's type (each item of a list property).</summary>
    private static void CheckValueList(object? value, RuleField field, List<string> problems)
    {
        var (expected, read) = field.Type switch
        {
            "String" or "ID" => ("text", (Func<object?, object?>)(v => v as string)),
            "Enum" => ($"value of {field.Path}", v => v is string s && (field.EnumValues is not { Count: > 0 } || field.EnumValues.Contains(s, StringComparer.Ordinal)) ? s : null),
            "Int" => ("whole number", v => Number(v) is { } n && n == Math.Floor(n) ? n : null),
            "Float" => ("number", v => Number(v)),
            _ => (null, null),
        };
        if (read is null)
        {
            problems.Add(ValuesNotForType(Kind(field.Type)));
            return;
        }

        if (AsList(value) is not { } values)
        {
            problems.Add(ValuesMustBeAList);
            return;
        }
        if (values.Count == 0)
        {
            problems.Add(ValuesMustNotBeEmpty);
            return;
        }
        if (values.Count > MaxListValues)
        {
            problems.Add(TooManyValues);
            return;
        }

        // Exact match, as Equal: "de" and "DE" are two values; 2 and 2.0 are one.
        var seen = new HashSet<object>();
        foreach (var item in values)
        {
            if (read(item) is not { } typed)
                problems.Add(ValueDoesNotFit(Show(item), expected!));
            else if (!seen.Add(typed))
                problems.Add(DuplicateValue(Show(item)));
        }
    }

    /// <summary>The items of a list value (a stored list, an array, or a JSON array from the request); null for anything else, text included.</summary>
    private static List<object?>? AsList(object? value) => value switch
    {
        null or string => null,
        JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray().Select(FromJson).ToList(),
        JsonElement => null,
        IEnumerable items => items.Cast<object?>().Select(item => item is JsonElement json ? FromJson(json) : item).ToList(),
        _ => null,
    };

    private static object? FromJson(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.String => json.GetString(),
        JsonValueKind.Number => json.TryGetInt64(out var whole) ? whole : json.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => json.GetRawText(),
    };

    /// <summary>A number, or text that reads as one in any culture; null otherwise.</summary>
    private static double? Number(object? value) => value switch
    {
        JsonElement json => Number(FromJson(json)),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture) is var d && double.IsFinite(d) ? d : null,
        string text => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null,
        _ => null,
    };

    private static long? WholeNumber(object? value) => Number(value) is { } n && n == Math.Floor(n) ? (long)n : null;

    private static string Show(object? value) => value switch
    {
        null => "empty",
        string text => text,
        _ => Number(value) is { } n ? n.ToString(CultureInfo.InvariantCulture) : value.ToString() ?? string.Empty,
    };

    private static string Kind(string type) => type switch
    {
        "DateTime" => "date-time",
        "Boolean" => "true/false",
        _ => SchemaTypeHelper.IsScalar(type) ? type : "object",
    };
}
