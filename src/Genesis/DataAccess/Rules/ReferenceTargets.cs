// Moved from blocks-data server/DataGateway.DomainService/Helpers/ReferenceTargets.cs (dev 3664eb77) so the gateway and the platform services share one rules engine.
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// Where a Reference rule points: the target schema's collection and the stored name of the target field. A system
/// entity's collection is written by the platform services into the tenant's own database, wherever the tenant's
/// other data lives.
/// </summary>
public sealed record ReferenceTarget(string SchemaName, string FieldName, string CollectionName, string DbFieldName, bool IsSystemEntity = false);

/// <summary>
/// Resolves Reference rules (value must exist in another schema's data) and Must not exist in rules (it must not)
/// against the schemas of one build. A mutation only sees its own schema, so the target's collection is looked up
/// here, once per build, from every schema in the snapshot.
/// </summary>
public sealed class ReferenceTargets
{
    public const string DefaultTargetField = nameof(GraphQlBaseEntity.ItemId);

    public static readonly ReferenceTargets Empty = new(new Dictionary<(string, string), ReferenceTarget>(), [], []);

    private readonly Dictionary<(string Schema, string Field), ReferenceTarget> _targets;
    private readonly HashSet<string> _sourceSchemaIds;
    // Filters naming a property the target doesn't have, by target and filter: their rules are left unchecked.
    private readonly HashSet<(string Schema, string Field, string Filter)> _uncheckedFilters;

    private ReferenceTargets(Dictionary<(string, string), ReferenceTarget> targets, HashSet<string> sourceSchemaIds, HashSet<(string, string, string)> uncheckedFilters)
    {
        _targets = targets;
        _sourceSchemaIds = sourceSchemaIds;
        _uncheckedFilters = uncheckedFilters;
    }

    /// <summary>Whether any field of <paramref name="schema"/> carries an active, resolved Reference or Must not exist in rule.</summary>
    public bool HasReferences(SchemaDefinitionExtended schema) => _sourceSchemaIds.Contains(schema.ItemId);

    /// <summary>The target of an active Reference or Must not exist in rule, or null when it could not be resolved at build time.</summary>
    public ReferenceTarget? Find(ValidationRule rule)
    {
        var key = (TargetSchemaName(rule), TargetFieldName(rule));
        if (rule.TargetFilter is not null && _uncheckedFilters.Contains((key.Item1, key.Item2, FilterKey(rule.TargetFilter))))
            return null;
        return _targets.GetValueOrDefault(key);
    }

    // Found once per filter object: Find runs for every value a mutation sends.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolicyRuleGroup, string> FilterKeys = new();

    private static string FilterKey(PolicyRuleGroup filter) => FilterKeys.GetValue(filter, f => f.ToJson());

    public static string TargetSchemaName(ValidationRule rule) => rule.Value?.ToString()?.Trim() ?? string.Empty;

    public static string TargetFieldName(ValidationRule rule)
    {
        var field = rule.SecondaryValue?.ToString()?.Trim();
        return string.IsNullOrEmpty(field) ? DefaultTargetField : field;
    }

    /// <summary>
    /// Resolves every active Reference and Must not exist in rule in <paramref name="schemas"/>. A rule whose target
    /// schema or field is missing, or whose filter names a property the target doesn't have, throws when
    /// <paramref name="strict"/> (a publish, so the admin sees why), and is otherwise logged and left unchecked
    /// (a pod rebuilding a version that was already published).
    /// </summary>
    public static ReferenceTargets Build(IReadOnlyCollection<SchemaDefinitionExtended> schemas, bool strict, ILogger logger)
    {
        var entities = schemas
            .Where(s => s.SchemaType == SchemaType.Entity)
            .GroupBy(s => s.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var targets = new Dictionary<(string, string), ReferenceTarget>();
        var sourceSchemaIds = new HashSet<string>();
        var uncheckedFilters = new HashSet<(string, string, string)>();
        var problems = new List<string>();

        foreach (var schema in entities.Values)
        {
            foreach (var (path, rule) in ReferenceRules(schema.Fields, string.Empty))
            {
                var problem = Resolve(entities, rule, out var target);
                if (problem is not null)
                {
                    problems.Add($"{schema.SchemaName}.{path} {problem}");
                    continue;
                }
                if (rule.TargetFilter is not null && FilterProblem(rule.TargetFilter, entities[TargetSchemaName(rule)]) is { } filterProblem)
                {
                    problems.Add($"{schema.SchemaName}.{path} {filterProblem}");
                    uncheckedFilters.Add((TargetSchemaName(rule), TargetFieldName(rule), FilterKey(rule.TargetFilter)));
                    continue;
                }
                targets[(TargetSchemaName(rule), TargetFieldName(rule))] = target!;
                sourceSchemaIds.Add(schema.ItemId);
            }
        }

        if (problems.Count > 0)
        {
            if (strict)
                throw new InvalidOperationException(string.Join("; ", problems));
            logger.LogWarning("Reference rules left unchecked because their target is missing: {Problems}", string.Join("; ", problems));
        }

        return targets.Count == 0 ? Empty : new ReferenceTargets(targets, sourceSchemaIds, uncheckedFilters);
    }

    /// <summary>The rules that look values up in another schema: Reference and Must not exist in.</summary>
    public static bool IsLookupRule(ValidationRule rule) =>
        rule.Type is ValidationType.Reference or ValidationType.NotExistsIn;

    private static string? Resolve(Dictionary<string, SchemaDefinitionExtended> entities, ValidationRule rule, out ReferenceTarget? target)
    {
        target = null;
        var schemaName = TargetSchemaName(rule);
        var fieldName = TargetFieldName(rule);
        if (!entities.TryGetValue(schemaName, out var targetSchema))
            return $"references schema '{schemaName}', which does not exist";

        // ItemId is stored as _id and is not always listed among a schema's fields.
        if (fieldName == DefaultTargetField)
        {
            target = new ReferenceTarget(targetSchema.SchemaName, fieldName, targetSchema.CollectionName, PolicyConstants.DbEntityIdFieldName, targetSchema.IsSystemEntity);
            return null;
        }

        var field = targetSchema.Fields.FirstOrDefault(f => f.Name == fieldName);
        if (field is null || field.IsArray || !SchemaTypeHelper.IsScalar(field.Type))
            return $"references field '{targetSchema.SchemaName}.{fieldName}', which does not exist or is not a single value";

        target = new ReferenceTarget(targetSchema.SchemaName, fieldName, targetSchema.CollectionName, fieldName, targetSchema.IsSystemEntity);
        return null;
    }

    /// <summary>
    /// Why a filter can't be checked as written, or null. Each condition must compare a property of the target with a
    /// fixed value. A property the target doesn't have (renamed or deleted) would make the filter match nothing, so a
    /// Reference would always fail and a Must not exist in always pass.
    /// </summary>
    private static string? FilterProblem(PolicyRuleGroup filter, SchemaDefinitionExtended target)
    {
        foreach (var condition in filter.Rules)
        {
            if (condition.LeftSource != ConditionSource.SCHEMA_FIELD || condition.RightSource != ConditionSource.STATIC_VALUE)
                return "has a filter that doesn't compare a property with a fixed value";
            if (condition.LeftOperand != DefaultTargetField && !HasProperty(target.Fields, condition.LeftOperand))
                return $"has a filter on '{condition.LeftOperand}', which isn't a property of '{target.SchemaName}'";
        }
        foreach (var nested in filter.NestedGroups)
        {
            if (FilterProblem(nested, target) is { } problem)
                return problem;
        }
        return null;
    }

    private static bool HasProperty(List<FieldDefinitionResponse> fields, string path)
    {
        // Nested properties are either stored as dotted rows ("Address.City") or under their object field.
        if (fields.Any(f => f.Name == path))
            return true;
        var dot = path.IndexOf('.');
        var field = fields.FirstOrDefault(f => f.Name == (dot < 0 ? path : path[..dot]));
        return field is not null && (dot < 0 || HasProperty(field.Fields, path[(dot + 1)..]));
    }

    /// <summary>Every active Reference and Must not exist in rule under <paramref name="fields"/>, with its dotted path.</summary>
    private static IEnumerable<(string Path, ValidationRule Rule)> ReferenceRules(List<FieldDefinitionResponse> fields, string prefix)
    {
        foreach (var field in fields)
        {
            var path = $"{prefix}{field.Name}";
            foreach (var rule in field.ValidationRule?.Validations ?? [])
            {
                if (rule.IsActive && IsLookupRule(rule))
                    yield return (path, rule);
            }
            foreach (var nested in ReferenceRules(field.Fields, $"{path}."))
                yield return nested;
        }
    }
}
