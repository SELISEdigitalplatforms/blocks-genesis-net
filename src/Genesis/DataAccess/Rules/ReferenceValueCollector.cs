// Moved from blocks-data server/DataGateway.DomainService/Helpers/ReferenceValueCollector.cs (dev 3664eb77) so the gateway and the platform services share one rules engine.
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>One value a mutation sends to a field with a Reference rule, and where it must exist.</summary>
public sealed record ReferenceValue(int InputIndex, string Path, object Value, ValidationRule Rule, ReferenceTarget Target);

/// <summary>
/// Finds the values in mutation inputs that Reference rules must check. Walks the input the same way
/// <see cref="DataValidationHelper"/> does: only the fields that were sent (so a partial update checks only
/// what it changes), nested objects as <c>a.b</c>, arrays of objects as <c>a[i].b</c>, and a list of plain
/// values element by element as <c>a[i]</c>. Null and blank values are left to Not empty / Required.
/// </summary>
public static class ReferenceValueCollector
{
    public static List<ReferenceValue> Collect(
        IReadOnlyList<Dictionary<string, object?>> inputs,
        SchemaDefinitionExtended schema,
        ReferenceTargets targets)
    {
        var found = new List<ReferenceValue>();
        for (var index = 0; index < inputs.Count; index++)
        {
            Walk(inputs[index], schema.Fields, string.Empty, index, targets, found);
        }
        return found;
    }

    private static void Walk(
        Dictionary<string, object?> input,
        List<FieldDefinitionResponse> fields,
        string prefix,
        int inputIndex,
        ReferenceTargets targets,
        List<ReferenceValue> found)
    {
        foreach (var (key, value) in input)
        {
            var field = fields.FirstOrDefault(f => f.Name == key);
            if (field is null || value is null)
            {
                continue;
            }

            var path = $"{prefix}{key}";
            if (!SchemaTypeHelper.IsScalar(field.Type))
            {
                WalkNested(field, value, path, inputIndex, targets, found);
                continue;
            }

            foreach (var rule in field.ValidationRule?.Validations ?? [])
            {
                if (!rule.IsActive || rule.Type != ValidationType.Reference || targets.Find(rule) is not { } target)
                {
                    continue;
                }

                var elements = field.IsArray ? DataValidationHelper.AsElements(value) : null;
                if (elements is null)
                {
                    Add(found, inputIndex, path, value, rule, target);
                    continue;
                }
                for (var i = 0; i < elements.Count; i++)
                {
                    Add(found, inputIndex, $"{path}[{i}]", elements[i], rule, target);
                }
            }
        }
    }

    private static void WalkNested(
        FieldDefinitionResponse field,
        object value,
        string path,
        int inputIndex,
        ReferenceTargets targets,
        List<ReferenceValue> found)
    {
        if (field.Fields.Count == 0)
        {
            return;
        }
        if (value is Dictionary<string, object?> nested)
        {
            Walk(nested, field.Fields, $"{path}.", inputIndex, targets, found);
            return;
        }
        if (field.IsArray && DataValidationHelper.AsElements(value) is { } items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is Dictionary<string, object?> item)
                    Walk(item, field.Fields, $"{path}[{i}].", inputIndex, targets, found);
            }
        }
    }

    private static void Add(List<ReferenceValue> found, int inputIndex, string path, object? value, ValidationRule rule, ReferenceTarget target)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        found.Add(new ReferenceValue(inputIndex, path, value, rule, target));
    }
}
