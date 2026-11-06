using System.Text.RegularExpressions;
using Blocks.Genesis.DataAccess.Evaluation;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>Where variables may appear.</summary>
public enum VariableMode
{
    /// <summary>A default value: text with any number of variables; both <c>{{Schema.X}}</c> and <c>{{Auth.X}}</c>.</summary>
    Template,

    /// <summary>A validation value: the whole value is one <c>{{Schema.X}}</c>, with no text around it and no Auth.</summary>
    SingleVariable,
}

/// <summary>One piece of parsed text: either plain text or a variable (<see cref="Prefix"/> and <see cref="Path"/>).</summary>
public sealed record VariablePart(string? Text, string? Prefix, string? Path, int Position)
{
    public bool IsVariable => Prefix is not null;
    public bool IsAuth => string.Equals(Prefix, VariableSyntax.AuthPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The variable as written in messages, e.g. <c>Employee.FirstName</c> or <c>Auth.UserId</c>.</summary>
    public string Name => $"{Prefix}.{Path}";
}

/// <summary>A syntax or reference problem, with the 1-based position of the variable or character at fault.</summary>
public sealed record VariableError(string Message, int Position);

/// <summary>What a variable may refer to: a property of the record, by its full dotted path.</summary>
public sealed record VariableField(string Type, bool IsArray, bool IsObject);

/// <summary>
/// The one variable grammar for default values and validation rules, used by the Data Gateway and by
/// the platform services. The client keeps a copy (<c>utils/variable-syntax.ts</c>) that must give the same
/// messages for the same input.
/// <code>
/// template := ( text | variable )*
/// variable := "{{" space* name space* "}}"
/// name     := "Auth." authName | schemaName "." segment ( "." segment )*
/// segment  := [A-Za-z_][A-Za-z0-9_]*
/// text     := any characters except "{" and "}"
/// </code>
/// </summary>
public static class VariableSyntax
{
    public const string AuthPrefix = "Auth";

    /// <summary>The caller values a template may use: the same ones the access-policy editor offers for its Auth source.</summary>
    public static readonly IReadOnlyList<string> AuthNames = ["UserId", "Email", "Roles", "Permissions", "OrganizationId"];

    private static readonly HashSet<string> AuthListNames = new(["Roles", "Permissions"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex NamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Splits <paramref name="text"/> into text and variables, checking the syntax only.
    /// Returns the parts, or the first syntax error.
    /// </summary>
    public static (IReadOnlyList<VariablePart> Parts, VariableError? Error) Parse(string text, VariableMode mode, string? schemaName = null)
    {
        var schema = string.IsNullOrWhiteSpace(schemaName) ? "Schema" : schemaName;
        var parts = new List<VariablePart>();
        var buffer = new System.Text.StringBuilder();
        var bufferStart = 0;
        var i = 0;

        void FlushText()
        {
            if (buffer.Length > 0)
                parts.Add(new VariablePart(buffer.ToString(), null, null, bufferStart + 1));
            buffer.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{')
            {
                if (i + 1 >= text.Length || text[i + 1] != '{')
                {
                    var inner = text[(i + 1)..].Split('}')[0].Trim();
                    var example = inner.Length > 0 ? $"{{{{{inner}}}}}" : $"{{{{{schema}.Property}}}}";
                    return (parts, new VariableError($"Single \"{{\" at position {i + 1}: variables are written {example}", i + 1));
                }

                var close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                var nextOpen = text.IndexOf("{{", i + 2, StringComparison.Ordinal);
                if (close < 0 || (nextOpen >= 0 && nextOpen < close))
                    return (parts, new VariableError($"Unclosed variable at position {i + 1}: expected \"}}}}\"", i + 1));

                var name = text[(i + 2)..close].Trim();
                if (name.Length == 0)
                    return (parts, new VariableError($"Empty variable at position {i + 1}", i + 1));
                if (name.Contains('{') || name.Contains('}'))
                    return (parts, new VariableError($"Unclosed variable at position {i + 1}: expected \"}}}}\"", i + 1));

                var nameError = CheckNameShape(name, i + 1, mode, schema);
                if (nameError is not null)
                    return (parts, nameError);

                FlushText();
                var dot = name.IndexOf('.');
                parts.Add(new VariablePart(null, name[..dot], name[(dot + 1)..], i + 1));
                i = close + 2;
                bufferStart = i;
                continue;
            }

            if (c == '}')
            {
                return text.AsSpan(i).StartsWith("}}")
                    ? (parts, new VariableError($"Unexpected \"}}}}\" at position {i + 1}", i + 1))
                    : (parts, new VariableError($"Single \"}}\" at position {i + 1}", i + 1));
            }

            if (buffer.Length == 0)
                bufferStart = i;
            buffer.Append(c);
            i++;
        }

        FlushText();

        if (mode == VariableMode.SingleVariable && parts.Any(p => p.IsVariable) && (parts.Count != 1 || !string.IsNullOrWhiteSpace(parts[0].Text)))
        {
            var example = parts.FirstOrDefault(p => p.IsVariable)?.Name ?? $"{schema}.Property";
            return (parts, new VariableError($"A validation value is either a fixed value or one variable, e.g. {{{{{example}}}}}", 1));
        }

        return (parts, null);
    }

    /// <summary>
    /// Checks what each variable refers to: the prefix must be <paramref name="schemaName"/> (or Auth in a
    /// template), and the property must exist in <paramref name="fields"/> and hold a single value.
    /// </summary>
    public static VariableError? CheckReferences(
        IReadOnlyList<VariablePart> parts,
        VariableMode mode,
        string schemaName,
        IReadOnlyDictionary<string, VariableField> fields,
        bool targetIsList)
    {
        foreach (var part in parts.Where(p => p.IsVariable))
        {
            if (part.IsAuth)
            {
                if (mode == VariableMode.SingleVariable)
                    return new VariableError($"Auth values aren't available in validation rules at position {part.Position}: use an access policy", part.Position);
                var authName = AuthNames.FirstOrDefault(n => string.Equals(n, part.Path, StringComparison.OrdinalIgnoreCase));
                if (authName is null)
                    return new VariableError($"Unknown value \"{part.Name}\" at position {part.Position}: use {string.Join(", ", AuthNames.Select(n => $"Auth.{n}"))}", part.Position);
                if (AuthListNames.Contains(authName) && !targetIsList)
                    return new VariableError($"Auth.{authName} is a list at position {part.Position}: use it in a list property", part.Position);
                continue;
            }

            if (!string.Equals(part.Prefix, schemaName, StringComparison.Ordinal))
            {
                var allowed = mode == VariableMode.Template ? $"{schemaName} or Auth" : schemaName;
                return new VariableError($"Unknown prefix \"{part.Prefix}\" at position {part.Position}: use {allowed}", part.Position);
            }

            if (!fields.TryGetValue(part.Path!, out var field))
            {
                var guess = ClosestName(part.Path!, fields.Where(f => !f.Value.IsObject).Select(f => f.Key));
                var hint = guess is null ? string.Empty : $" (did you mean {schemaName}.{guess}?)";
                return new VariableError($"Unknown property \"{part.Name}\" at position {part.Position}{hint}", part.Position);
            }

            if (field.IsObject)
            {
                var child = fields.Keys.FirstOrDefault(k => k.StartsWith(part.Path + ".", StringComparison.Ordinal) && !fields[k].IsObject);
                var example = child is null ? string.Empty : $", e.g. {{{{{schemaName}.{child}}}}}";
                return new VariableError($"\"{part.Name}\" holds an object at position {part.Position}: use one of its properties{example}", part.Position);
            }
        }

        return null;
    }

    /// <summary>
    /// The value of a variable: the record's property (from <paramref name="input"/>, then
    /// <paramref name="stored"/>), or the caller's value through the policy engine's own resolver.
    /// Null when there is no value.
    /// </summary>
    public static object? Resolve(
        VariablePart variable,
        IReadOnlyDictionary<string, object?> input,
        IReadOnlyDictionary<string, object?>? stored,
        AccessPrincipal? principal)
    {
        if (variable.IsAuth)
            return principal is null ? null : DataAccessPolicyHelper.GetTokenValue(principal, variable.Path!);

        return ReadPath(input, variable.Path!) ?? (stored is null ? null : ReadPath(stored, variable.Path!));
    }

    /// <summary>True when <paramref name="value"/> is exactly one variable, e.g. a validation bound of <c>{{Employee.StartDate}}</c>.</summary>
    public static bool TryGetSingleVariable(object? value, out VariablePart variable)
    {
        variable = null!;
        if (value is not string text || !text.Contains("{{", StringComparison.Ordinal))
            return false;
        var (parts, error) = Parse(text, VariableMode.SingleVariable, null);
        if (error is not null || parts.Count != 1 || !parts[0].IsVariable)
            return false;
        variable = parts[0];
        return true;
    }

    /// <summary>The variables a record may refer to, from its schema's field tree (nested fields by full dotted path).</summary>
    public static Dictionary<string, VariableField> FieldsOf(IEnumerable<FieldDefinitionResponse> fields, string prefix = "")
    {
        var result = new Dictionary<string, VariableField>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var path = $"{prefix}{field.Name}";
            var isObject = !SchemaTypeHelper.IsScalar(field.Type);
            result[path] = new VariableField(field.Type, field.IsArray, isObject);
            if (isObject)
            {
                foreach (var (childPath, child) in FieldsOf(field.Fields, $"{path}."))
                    result[childPath] = child;
            }
        }
        return result;
    }

    /// <summary>The variables a record may refer to, from stored field definitions (nested fields are dotted rows).</summary>
    public static Dictionary<string, VariableField> FieldsOf(IEnumerable<Entities.Data.FieldDefinition> fields)
    {
        var result = new Dictionary<string, VariableField>(StringComparer.Ordinal);
        foreach (var field in fields)
            result[field.Name] = new VariableField(field.Type, field.IsArray, !SchemaTypeHelper.IsScalar(field.Type));
        return result;
    }

    internal static object? ReadPath(IReadOnlyDictionary<string, object?> record, string path)
    {
        object? current = record;
        foreach (var segment in path.Split('.'))
        {
            if (current is IReadOnlyDictionary<string, object?> map && map.TryGetValue(segment, out var next))
                current = next;
            else if (current is IDictionary<string, object?> dict && dict.TryGetValue(segment, out var value))
                current = value;
            else
                return null;
        }
        return current;
    }

    private static VariableError? CheckNameShape(string name, int position, VariableMode mode, string schema)
    {
        if (!name.Contains('.'))
        {
            var target = mode == VariableMode.Template ? $"{{{{{schema}.{name}}}}} or {{{{Auth.…}}}}" : $"{{{{{schema}.{name}}}}}";
            return IsSegment(name)
                ? new VariableError($"Missing prefix at position {position}: write {target}", position)
                : new VariableError($"Invalid name \"{name}\" at position {position}: use letters, digits, \"_\" and \".\" between parts", position);
        }

        return NamePattern.IsMatch(name)
            ? null
            : new VariableError($"Invalid name \"{name}\" at position {position}: use letters, digits, \"_\" and \".\" between parts", position);
    }

    private static bool IsSegment(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static string? ClosestName(string wanted, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Distance(wanted.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return bestDistance <= 2 ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return row[b.Length];
    }
}
