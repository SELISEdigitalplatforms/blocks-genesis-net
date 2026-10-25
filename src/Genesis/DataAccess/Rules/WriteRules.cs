using System.Globalization;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// The pieces of <see cref="IDataAccessResolver.ResolveWriteAsync"/> that need no database: the system-schema
/// allow-list filter, BSON ↔ dictionary conversion, and applying defaults to a document.
/// </summary>
public static class WriteRules
{
    /// <summary>
    /// Drops every rule and default the allow-list doesn't open (system schemas only), so a value written
    /// straight into <c>DataValidations</c> or <c>SchemaDefinitions</c> has no effect. Each dropped one is
    /// reported to <paramref name="ignored"/> as <c>"Schema.Path validation"</c> or <c>"Schema.Path default"</c>.
    /// Returns true when anything is left to do: a rule or a default.
    /// </summary>
    public static bool KeepAllowedOnly(SchemaDefinitionExtended schema, Action<string>? ignored = null)
    {
        var rules = schema.IsSystemEntity ? SystemSchemaRules.For(schema.SchemaName) : null;
        if (schema.IsSystemEntity && rules is null)
        {
            Strip(schema, schema.Fields, string.Empty, _ => false, _ => false, ignored);
            return false;
        }

        if (rules is not null)
        {
            Strip(schema, schema.Fields, string.Empty,
                path => rules.ValidationFor(path) != RuleAccess.Blocked,
                path => rules.DefaultFor(path) != RuleAccess.Blocked,
                ignored);
        }
        return HasAnything(schema.Fields);
    }

    private static void Strip(SchemaDefinitionExtended schema, List<FieldDefinitionResponse> fields, string prefix,
        Func<string, bool> validationAllowed, Func<string, bool> defaultAllowed, Action<string>? ignored)
    {
        foreach (var field in fields)
        {
            var path = $"{prefix}{field.Name}";
            if (field.ValidationRule is not null && !validationAllowed(path))
            {
                if (field.ValidationRule.Validations.Any(r => r.IsActive))
                    ignored?.Invoke($"{schema.SchemaName}.{path} validation");
                field.ValidationRule = null;
            }
            if (field.DefaultValue is not null && !defaultAllowed(path))
            {
                ignored?.Invoke($"{schema.SchemaName}.{path} default");
                field.DefaultValue = null;
            }
            Strip(schema, field.Fields, $"{path}.", validationAllowed, defaultAllowed, ignored);
        }
    }

    private static bool HasAnything(IEnumerable<FieldDefinitionResponse> fields) =>
        fields.Any(f => f.DefaultValue is not null
                        || f.ValidationRule?.Validations.Any(r => r.IsActive) == true
                        || HasAnything(f.Fields));

    /// <summary>
    /// The document as rule input: nested documents as dictionaries, arrays as lists. Null values are left
    /// out, as the gateway leaves out nulls a client sends, so a rule never fails on a property the code
    /// left empty (a default still fills it).
    /// </summary>
    public static Dictionary<string, object?> ToRecord(BsonDocument document)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var element in document)
        {
            if (!element.Value.IsBsonNull)
                record[element.Name] = ToValue(element.Value);
        }
        return record;
    }

    private static object? ToValue(BsonValue value) => value switch
    {
        BsonDocument nested => ToRecord(nested),
        BsonArray array => array.Select(ToValue).ToList(),
        BsonNull => null,
        BsonDateTime date => date.ToUniversalTime(),
        _ => BsonTypeMapper.MapToDotNetValue(value),
    };

    /// <summary>Writes defaults into <paramref name="document"/>, only where the property is missing or null. Returns how many were filled.</summary>
    public static int ApplyDefaults(BsonDocument document, IReadOnlyList<FieldDefaultsApplier.DefaultAssignment> defaults)
    {
        var filled = 0;
        foreach (var assignment in defaults)
        {
            BsonValue container = document;
            for (var i = 0; i < assignment.Path.Count - 1; i++)
            {
                container = (assignment.Path[i], container) switch
                {
                    (string key, BsonDocument map) when map.TryGetValue(key, out var next) && next is BsonDocument or BsonArray => next,
                    (string key, BsonDocument map) => map[key] = new BsonDocument(),
                    (int index, BsonArray list) when index < list.Count => list[index],
                    _ => BsonNull.Value,
                };
                if (container.IsBsonNull)
                    break;
            }

            if (container is BsonDocument target && assignment.Path[^1] is string last
                && (!target.TryGetValue(last, out var current) || current.IsBsonNull))
            {
                target[last] = BsonValue.Create(assignment.Value);
                filled++;
            }
        }
        return filled;
    }

    /// <summary>
    /// The comparison key for a referenced value: numbers by value (Int 3 matches Float 3.0, as Mongo's
    /// <c>$in</c> does), everything else by its text. Shared with the Data Gateway.
    /// </summary>
    public static string ReferenceKey(BsonValue value) =>
        value.IsNumeric
            ? "n:" + value.ToDouble().ToString("R", CultureInfo.InvariantCulture)
            : "s:" + (value.IsString ? value.AsString : value.ToString());

    /// <summary>The default message for a value a Reference rule did not find. Shared with the Data Gateway.</summary>
    public static string ReferenceNotFoundMessage(string schemaName, ReferenceValue value) =>
        string.IsNullOrWhiteSpace(value.Rule.ErrorMessage)
            ? $"Value '{value.Value}' of {schemaName}.{value.Path} was not found in {value.Target.SchemaName}.{value.Target.FieldName}."
            : value.Rule.ErrorMessage;

    /// <summary>The default message for a value a Must not exist in rule found. Shared with the Data Gateway.</summary>
    public static string ReferenceFoundMessage(string schemaName, ReferenceValue value) =>
        string.IsNullOrWhiteSpace(value.Rule.ErrorMessage)
            ? $"Value '{value.Value}' of {schemaName}.{value.Path} already exists in {value.Target.SchemaName}.{value.Target.FieldName}."
            : value.Rule.ErrorMessage;

    /// <summary>Names of the schemas the active Reference and Must not exist in rules in <paramref name="fields"/> point at.</summary>
    public static HashSet<string> ReferencedSchemaNames(IEnumerable<FieldDefinitionResponse> fields)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            foreach (var rule in field.ValidationRule?.Validations ?? [])
            {
                if (rule.IsActive && ReferenceTargets.IsLookupRule(rule))
                    names.Add(ReferenceTargets.TargetSchemaName(rule));
            }
            names.UnionWith(ReferencedSchemaNames(field.Fields));
        }
        return names;
    }
}
