using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using MongoDB.Bson.Serialization;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>How careful admins must be with a system schema or property.</summary>
public enum RuleLevel
{
    Normal,

    /// <summary>Used by sign-in, mail or the console: the OS console asks before saving a rule or default.</summary>
    Warned,
}

/// <summary>Whether validation rules or a default value may be set on a property.</summary>
public enum RuleAccess
{
    /// <summary>The default: not offered in the console, refused by the API, ignored by the engine.</summary>
    Blocked = 0,
    Allowed,

    /// <summary>Allowed, but the OS console asks first.</summary>
    Warned,
}

/// <summary>
/// Marks a Genesis system entity whose properties may get validation rules or default values from the
/// OS Data Access page. A class without it is excluded entirely.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SystemSchemaRulesAttribute(RuleLevel level) : Attribute
{
    public RuleLevel Level { get; } = level;
}

/// <summary>
/// Opens a property of a system entity to validation rules and/or default values. A property without it
/// is blocked. On a nested object (e.g. <c>Read</c>), its own properties (<c>Read.Roles</c>…) inherit the access.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AllowRulesAttribute : Attribute
{
    public RuleAccess Validation { get; init; }
    public RuleAccess Default { get; init; }
}

/// <summary>What may be set on one property, by the path the seeded <c>SchemaDefinitions</c> use (e.g. <c>Read.Roles</c>).</summary>
public sealed record SystemFieldRules(string Path, RuleAccess Validation, RuleAccess Default, bool DefaultHasNoEffect);

/// <summary>A system schema's level and the properties open to rules; anything not listed is blocked.</summary>
public sealed record SystemSchemaRuleSet(string SchemaName, RuleLevel Level, IReadOnlyDictionary<string, SystemFieldRules> Fields)
{
    public RuleAccess ValidationFor(string path) => Fields.TryGetValue(path, out var field) ? field.Validation : RuleAccess.Blocked;
    public RuleAccess DefaultFor(string path) => Fields.TryGetValue(path, out var field) ? field.Default : RuleAccess.Blocked;
}

/// <summary>
/// Reads the allow-list from the attributes on the Genesis entity classes. Paths are built exactly as the
/// system-entity seed names fields (DATA_ACCESS_README §7.5): the schema name is the class name, a field
/// is its <see cref="BsonClassMap"/> element name (the id as <c>ItemId</c>), and nested properties are
/// <c>Parent.Child</c>, at most 3 levels deep. Consult it only for <c>IsSystemEntity</c> schemas.
/// </summary>
public static class SystemSchemaRules
{
    private const int MaxDepth = 3;
    private static readonly Lazy<IReadOnlyDictionary<string, Type>> Types = new(FindTypes);
    private static readonly ConcurrentDictionary<string, SystemSchemaRuleSet?> Cache = new(StringComparer.Ordinal);

    /// <summary>Names of the system schemas that may have rules or defaults.</summary>
    public static IReadOnlyCollection<string> SchemaNames => Types.Value.Keys.ToList();

    /// <summary>The schema's rules, or null when the schema is excluded (or not a Genesis system entity).</summary>
    public static SystemSchemaRuleSet? For(string schemaName) =>
        Cache.GetOrAdd(schemaName, name => Types.Value.TryGetValue(name, out var type) ? Build(name, type) : null);

    private static IReadOnlyDictionary<string, Type> FindTypes() =>
        typeof(SystemSchemaRules).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace?.StartsWith("Blocks.Genesis.Entities", StringComparison.Ordinal) == true
                        && t.GetCustomAttribute<SystemSchemaRulesAttribute>() is not null)
            .ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);

    private static SystemSchemaRuleSet Build(string name, Type type)
    {
        var level = type.GetCustomAttribute<SystemSchemaRulesAttribute>()!.Level;
        var fields = new Dictionary<string, SystemFieldRules>(StringComparer.Ordinal);
        Walk(type, prefix: string.Empty, inherited: null, depth: 1, fields, ancestors: [type]);
        return new SystemSchemaRuleSet(name, level, fields);
    }

    private static void Walk(Type type, string prefix, AllowRulesAttribute? inherited, int depth, Dictionary<string, SystemFieldRules> fields, HashSet<Type> ancestors)
    {
        var classMap = BsonClassMap.LookupClassMap(type);
        foreach (var member in classMap.AllMemberMaps)
        {
            if (member.MemberInfo is not PropertyInfo property)
                continue;

            var elementName = member == classMap.IdMemberMap ? property.Name : member.ElementName;
            var path = $"{prefix}{elementName}";
            var access = property.GetCustomAttribute<AllowRulesAttribute>(inherit: true) ?? inherited;
            if (access is null)
                continue;

            var nested = NestedClass(property.PropertyType);
            if (nested is not null)
            {
                if (depth < MaxDepth && !ancestors.Contains(nested))
                    Walk(nested, $"{path}.", access, depth + 1, fields, [.. ancestors, nested]);
                continue;
            }

            if (access.Validation == RuleAccess.Blocked && access.Default == RuleAccess.Blocked)
                continue;
            fields[path] = new SystemFieldRules(path, access.Validation, access.Default, DefaultHasNoEffect(property));
        }
    }

    /// <summary>
    /// The class a property holds (or holds a list of), when the seed expands it into <c>Parent.Child</c>
    /// fields; null for values, text, enums, dictionaries and system types.
    /// </summary>
    private static Type? NestedClass(Type type)
    {
        var element = Nullable.GetUnderlyingType(type) ?? type;
        if (element != typeof(string) && typeof(IEnumerable).IsAssignableFrom(element))
        {
            if (element.IsArray)
                element = element.GetElementType()!;
            else if (element.IsGenericType && element.GetGenericArguments() is [var item] && !typeof(IDictionary).IsAssignableFrom(element))
                element = item;
            else
                return null;
        }
        var isOwnClass = element.IsClass && element != typeof(string) && element != typeof(object)
                         && element.Namespace?.StartsWith("Blocks.Genesis", StringComparison.Ordinal) == true;
        return isOwnClass ? element : null;
    }

    /// <summary>A typed entity always has a value for non-nullable values and text, so a default there is never used.</summary>
    private static bool DefaultHasNoEffect(PropertyInfo property)
    {
        var type = property.PropertyType;
        if (type.IsValueType)
            return Nullable.GetUnderlyingType(type) is null;
        // NullabilityInfoContext isn't thread-safe; rule sets may be built concurrently.
        return type == typeof(string) && new NullabilityInfoContext().Create(property).WriteState == NullabilityState.NotNull;
    }
}
