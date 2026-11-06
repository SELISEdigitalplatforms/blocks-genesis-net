using System.Collections;
using System.Globalization;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// Turns a stored default (as it comes back from Mongo or JSON: long, double, string, object list…) into
/// the value an insert would carry for the property's type: string, int, double, bool, UTC DateTime,
/// or a List of those for a list property.
/// </summary>
public static class DefaultValueConverter
{
    public static readonly IReadOnlySet<string> SupportedTypes =
        new HashSet<string>(["String", "ID", "Int", "Float", "Boolean", "DateTime", SchemaTypeHelper.EnumTypeName], StringComparer.Ordinal);

    /// <summary>Converts one item. Returns false with a user-facing message when it doesn't fit the type.</summary>
    public static bool TryConvert(object? raw, string type, IReadOnlyCollection<string>? enumValues, out object? value, out string? error)
    {
        value = null;
        error = null;
        var text = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        switch (type)
        {
            case "Int":
                // Stored numbers come back as long or double; text is parsed as typed.
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                {
                    value = whole;
                    return true;
                }
                if (raw is double d && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
                {
                    value = (int)d;
                    return true;
                }
                error = $"\"{text}\" is not a whole number";
                return false;
            case "Float":
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                {
                    value = number;
                    return true;
                }
                error = $"\"{text}\" is not a number";
                return false;
            case "Boolean":
                if (raw is bool flag || bool.TryParse(text, out flag))
                {
                    value = flag;
                    return true;
                }
                error = $"\"{text}\" is not true or false";
                return false;
            case "DateTime":
                if (raw is DateTime date)
                {
                    value = date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
                    return true;
                }
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
                {
                    value = date;
                    return true;
                }
                error = $"\"{text}\" is not a valid date-time";
                return false;
            case SchemaTypeHelper.EnumTypeName:
                if (enumValues is { Count: > 0 } && !enumValues.Contains(text))
                {
                    error = $"\"{text}\" is not one of the listed values ({string.Join(", ", enumValues)})";
                    return false;
                }
                value = text;
                return true;
            default:
                value = text;
                return true;
        }
    }

    /// <summary>The items of a stored list value (object[], List, BsonArray-as-list…); null when it isn't a list.</summary>
    public static List<object?>? AsList(object? raw) =>
        raw is IEnumerable list and not string ? list.Cast<object?>().ToList() : null;
}

/// <summary>
/// Checks a default value before it is saved: by the Data Gateway API, the OS Data Access API and schema
/// import. Returns the first problem as a user-facing message, or null when the default can be saved.
/// </summary>
public static class FieldDefaultValueCheck
{
    /// <summary>Set by the platform on every save; never defaulted.</summary>
    public static readonly IReadOnlySet<string> LockedProperties =
        new HashSet<string>(["ItemId", "CreatedBy", "LastUpdatedBy", "CreatedDate", "LastUpdatedDate"], StringComparer.Ordinal);

    public static string? Check(
        string fieldName,
        string fieldType,
        bool isArray,
        IReadOnlyCollection<string>? enumValues,
        FieldDefaultValue defaultValue,
        string schemaName,
        IReadOnlyDictionary<string, VariableField> fields)
    {
        if (LockedProperties.Contains(fieldName))
            return $"{fieldName} is set by the platform and can't have a default value";
        if (!DefaultValueConverter.SupportedTypes.Contains(fieldType))
            return $"Default values can't be set on {fieldType} properties";

        var display = isArray ? $"{fieldName}[]" : fieldName;
        switch (defaultValue.Kind)
        {
            case DefaultValueKind.Static:
                return CheckStatic(display, fieldType, isArray, enumValues, defaultValue.Value);
            case DefaultValueKind.Template:
                return CheckTemplate(display, fieldType, isArray, defaultValue.Value, schemaName, fields);
            case DefaultValueKind.Now:
                if (fieldType != "DateTime" || isArray)
                    return "Now can only be used on single date-time properties";
                return null;
            default:
                return "Choose Static, Template or Now";
        }
    }

    private static string? CheckStatic(string display, string type, bool isArray, IReadOnlyCollection<string>? enumValues, object? raw)
    {
        if (raw is null || raw is string { Length: 0 } && type != "String")
            return "A value is required";

        var items = DefaultValueConverter.AsList(raw);
        if (isArray && items is null)
            return $"A list is expected for {display}";
        if (!isArray && items is not null)
            return $"A single value is expected for {display}";

        foreach (var item in items ?? [raw])
        {
            if (!DefaultValueConverter.TryConvert(item, type, enumValues, out _, out var error))
                return error;
        }
        return null;
    }

    private static string? CheckTemplate(string display, string type, bool isArray, object? raw, string schemaName, IReadOnlyDictionary<string, VariableField> fields)
    {
        if (type is not ("String" or "ID"))
            return "Templates can only be used on text properties";

        var items = DefaultValueConverter.AsList(raw);
        if (isArray && items is null)
            return $"A list is expected for {display}";
        if (!isArray && (items is not null || raw is not string))
            return $"A single template is expected for {display}";

        foreach (var item in items ?? [raw])
        {
            var text = item as string ?? string.Empty;
            var (parts, error) = VariableSyntax.Parse(text, VariableMode.Template, schemaName);
            error ??= VariableSyntax.CheckReferences(parts, VariableMode.Template, schemaName, fields, targetIsList: isArray);
            if (error is not null)
                return error.Message;
        }
        return null;
    }
}

/// <summary>
/// Applies default values on insert: a property that is missing or null gets its default; a value the
/// caller sent always wins. Fixed and Now defaults are worked out first, then templates, which may read
/// them (but not other templates).
/// </summary>
public static class FieldDefaultsApplier
{
    /// <summary>A default to set, addressed by path segments: property names, and item indexes inside lists of objects.</summary>
    public sealed record DefaultAssignment(IReadOnlyList<object> Path, object Value);

    public static bool HasDefaults(IEnumerable<FieldDefinitionResponse> fields) =>
        fields.Any(f => f.DefaultValue is not null || HasDefaults(f.Fields));

    /// <summary>
    /// Every default of the record, worked out once. Nothing is changed; pass the result to <see cref="Apply"/>,
    /// which fills only what is missing. Defaults of properties the caller sent are included too, so they
    /// can fill in after column access rules removed a value the caller may not write.
    /// </summary>
    public static List<DefaultAssignment> Compute(
        IReadOnlyDictionary<string, object?> input,
        string schemaName,
        IReadOnlyList<FieldDefinitionResponse> fields,
        AccessPrincipal? principal,
        DateTime nowUtc)
    {
        var fixedOnes = new List<DefaultAssignment>();
        var templates = new List<(IReadOnlyList<object> Path, FieldDefinitionResponse Field)>();
        Walk(input, fields, [], fixedOnes, templates, nowUtc);

        if (templates.Count == 0)
            return fixedOnes;

        // Templates read the record as it will be stored: the input plus the fixed and Now defaults.
        var view = DeepCopy(input);
        Apply(view, fixedOnes);

        var all = new List<DefaultAssignment>(fixedOnes);
        foreach (var (path, field) in templates)
        {
            var value = RenderTemplate(field, schemaName, view, principal);
            if (value is not null)
                all.Add(new DefaultAssignment(path, value));
        }
        return all;
    }

    /// <summary>
    /// Sets each default whose property is still missing or null, creating parent objects as needed.
    /// Safe to call again after access rules removed values: only missing ones are filled.
    /// </summary>
    public static void Apply(IDictionary<string, object?> input, IReadOnlyList<DefaultAssignment> defaults)
    {
        foreach (var assignment in defaults)
        {
            object? container = input;
            for (var i = 0; i < assignment.Path.Count - 1; i++)
            {
                container = assignment.Path[i] switch
                {
                    string key when container is IDictionary<string, object?> map =>
                        map.TryGetValue(key, out var next) && next is not null ? next : map[key] = new Dictionary<string, object?>(),
                    int index when container is IList list && index < list.Count => list[index],
                    _ => null,
                };
                if (container is null)
                    break;
            }

            if (container is IDictionary<string, object?> target && assignment.Path[^1] is string last
                && (!target.TryGetValue(last, out var current) || current is null))
            {
                target[last] = assignment.Value;
            }
        }
    }

    private static void Walk(
        IReadOnlyDictionary<string, object?> input,
        IReadOnlyList<FieldDefinitionResponse> fields,
        IReadOnlyList<object> prefix,
        List<DefaultAssignment> fixedOnes,
        List<(IReadOnlyList<object>, FieldDefinitionResponse)> templates,
        DateTime nowUtc)
    {
        foreach (var field in fields)
        {
            var path = prefix.Append(field.Name).ToList();
            input.TryGetValue(field.Name, out var sent);

            if (!SchemaTypeHelper.IsScalar(field.Type))
            {
                if (field.Fields.Count == 0)
                    continue;
                if (field.IsArray)
                {
                    // Inside a list of objects: each item that was sent; no items are created.
                    if (DefaultValueConverter.AsList(sent) is { } items)
                    {
                        for (var i = 0; i < items.Count; i++)
                        {
                            if (items[i] is IReadOnlyDictionary<string, object?> item)
                                Walk(item, field.Fields, path.Append((object)i).ToList(), fixedOnes, templates, nowUtc);
                            else if (items[i] is IDictionary<string, object?> dict)
                                Walk(new Dictionary<string, object?>(dict), field.Fields, path.Append((object)i).ToList(), fixedOnes, templates, nowUtc);
                        }
                    }
                    continue;
                }
                var nested = sent as IReadOnlyDictionary<string, object?>
                    ?? (sent is IDictionary<string, object?> map ? new Dictionary<string, object?>(map) : new Dictionary<string, object?>());
                Walk(nested, field.Fields, path, fixedOnes, templates, nowUtc);
                continue;
            }

            if (field.DefaultValue is not { } defaultValue)
                continue;

            switch (defaultValue.Kind)
            {
                case DefaultValueKind.Static when StaticValue(field, defaultValue.Value) is { } value:
                    fixedOnes.Add(new DefaultAssignment(path, value));
                    break;
                case DefaultValueKind.Now:
                    fixedOnes.Add(new DefaultAssignment(path, nowUtc + (defaultValue.Offset?.ToTimeSpan() ?? TimeSpan.Zero)));
                    break;
                case DefaultValueKind.Template:
                    templates.Add((path, field));
                    break;
            }
        }
    }

    private static object? StaticValue(FieldDefinitionResponse field, object? raw)
    {
        if (field.IsArray)
        {
            var items = DefaultValueConverter.AsList(raw);
            if (items is null)
                return null;
            var converted = new List<object?>();
            foreach (var item in items)
            {
                if (DefaultValueConverter.TryConvert(item, field.Type, field.EnumValues, out var value, out _))
                    converted.Add(value);
            }
            return converted;
        }
        return DefaultValueConverter.TryConvert(raw, field.Type, field.EnumValues, out var single, out _) ? single : null;
    }

    private static object? RenderTemplate(FieldDefinitionResponse field, string schemaName, IReadOnlyDictionary<string, object?> view, AccessPrincipal? principal)
    {
        if (field.IsArray)
        {
            var items = new List<object?>();
            foreach (var template in DefaultValueConverter.AsList(field.DefaultValue!.Value) ?? [])
            {
                var (parts, error) = VariableSyntax.Parse(template as string ?? string.Empty, VariableMode.Template, schemaName);
                if (error is not null)
                    continue;
                // A lone list variable (e.g. {{Auth.Roles}}) adds every item; anything else adds one text item.
                if (parts is [{ IsVariable: true } only] && VariableSyntax.Resolve(only, view, null, principal) is IEnumerable list and not string)
                {
                    items.AddRange(list.Cast<object?>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)).Where(v => !string.IsNullOrWhiteSpace(v)));
                    continue;
                }
                var text = Render(parts, view, principal);
                if (!string.IsNullOrWhiteSpace(text))
                    items.Add(text);
            }
            return items.Count == 0 ? null : items;
        }

        var (single, singleError) = VariableSyntax.Parse(field.DefaultValue!.Value as string ?? string.Empty, VariableMode.Template, schemaName);
        if (singleError is not null)
            return null;
        var rendered = Render(single, view, principal);
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    private static string Render(IReadOnlyList<VariablePart> parts, IReadOnlyDictionary<string, object?> view, AccessPrincipal? principal)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (!part.IsVariable)
            {
                builder.Append(part.Text);
                continue;
            }
            var value = VariableSyntax.Resolve(part, view, null, principal);
            builder.Append(value switch
            {
                null => string.Empty,
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                IEnumerable list and not string => string.Join(", ", list.Cast<object?>()),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            });
        }
        // Missing parts leave doubled or edge spaces behind ("Ann " for a missing LastName).
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static Dictionary<string, object?> DeepCopy(IReadOnlyDictionary<string, object?> source) =>
        source.ToDictionary(kv => kv.Key, kv => Copy(kv.Value));

    private static object? Copy(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> map => DeepCopy(map),
        IDictionary<string, object?> dict => DeepCopy(new Dictionary<string, object?>(dict)),
        IEnumerable list and not string => list.Cast<object?>().Select(Copy).ToList(),
        _ => value,
    };
}
