using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Lookups;

/// <summary>
/// Where an EXISTS_IN / NOT_EXISTS_IN rule looks: the target's collection and the stored name of the target field.
/// <see cref="InvalidReason"/> is set when the rule no longer fits its target; such a rule never matches and makes
/// no query.
/// </summary>
public sealed record AccessLookupTarget(string SchemaName, string FieldName, string CollectionName, string DbFieldName, bool IsSystemEntity, string? InvalidReason);

/// <summary>
/// The targets of one schema's lookup rules, checked once when the schema is assembled against the definitions of
/// the schemas they name (<see cref="TargetSchemaNames"/>). Keyed by the rule's <see cref="PolicyReference"/>
/// instance: <see cref="Find"/> answers only for the references of the schema it was built from (the column copies
/// derived from a policy share its rule group, so they share the references too).
/// </summary>
public sealed class AccessLookupTargets
{
    private readonly Dictionary<PolicyReference, AccessLookupTarget> _targets;

    private AccessLookupTargets(Dictionary<PolicyReference, AccessLookupTarget> targets) => _targets = targets;

    /// <summary>The target of <paramref name="reference"/>, or null when it is not a reference of the schema these targets were built from.</summary>
    public AccessLookupTarget? Find(PolicyReference reference) =>
        reference is not null && _targets.TryGetValue(reference, out var target) ? target : null;

    /// <summary>The schema names the lookup rules of <paramref name="policySchema"/>'s policies point at, distinct, in order.</summary>
    public static IReadOnlyCollection<string> TargetSchemaNames(SchemaDefinitionExtended policySchema)
    {
        ArgumentNullException.ThrowIfNull(policySchema);
        return LookupRules(policySchema)
            .Select(r => r.Reference?.SchemaName?.Trim())
            .OfType<string>()
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Checks every lookup rule of <paramref name="policySchema"/> against <paramref name="candidates"/> (the policy
    /// schema itself is always a candidate, for rules that look into their own schema).
    /// </summary>
    public static AccessLookupTargets Build(SchemaDefinitionExtended policySchema, IReadOnlyCollection<SchemaDefinitionExtended> candidates)
    {
        ArgumentNullException.ThrowIfNull(policySchema);
        ArgumentNullException.ThrowIfNull(candidates);

        var targets = new Dictionary<PolicyReference, AccessLookupTarget>(ReferenceEqualityComparer.Instance);
        foreach (var rule in LookupRules(policySchema))
        {
            if (rule.Reference is null)
                continue;
            var target = Check(policySchema, candidates, rule);
            // The same reference under two rules (never expected): the stricter answer wins.
            if (!targets.TryGetValue(rule.Reference, out var known) || known.InvalidReason is null)
                targets[rule.Reference] = target;
        }
        return new AccessLookupTargets(targets);
    }

    private static IEnumerable<PolicyRule> LookupRules(SchemaDefinitionExtended schema) =>
        schema.Policies
            .Where(p => p.RuleGroup is not null)
            .SelectMany(p => Walk(p.RuleGroup))
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<PolicyRule>();

    private static IEnumerable<PolicyRule> Walk(PolicyRuleGroup group)
    {
        foreach (var rule in group.Rules)
            if (rule.Operator is PolicyOperator.EXISTS_IN or PolicyOperator.NOT_EXISTS_IN)
                yield return rule;
        foreach (var nested in group.NestedGroups)
            foreach (var rule in Walk(nested))
                yield return rule;
    }

    private static AccessLookupTarget Check(SchemaDefinitionExtended policySchema, IReadOnlyCollection<SchemaDefinitionExtended> candidates, PolicyRule rule)
    {
        var reference = rule.Reference!;
        var schemaName = reference.SchemaName?.Trim() ?? string.Empty;
        var fieldName = string.IsNullOrWhiteSpace(reference.FieldName) ? ReferenceTargets.DefaultTargetField : reference.FieldName.Trim();

        AccessLookupTarget Invalid(string reason, SchemaDefinitionExtended? target = null) =>
            new(target?.SchemaName ?? schemaName, fieldName, target?.CollectionName ?? string.Empty,
                ReferenceTargets.StoredFieldName(fieldName), target?.IsSystemEntity ?? false, reason);

        var (target, problem) = FindTarget(policySchema, candidates, schemaName);
        if (target is null)
            return Invalid(problem!);

        if (policySchema.IsSystemEntity && !target.IsSystemEntity)
            return Invalid($"looks into tenant schema '{target.SchemaName}', but a policy on a system schema may only look into system schemas", target);

        if (string.IsNullOrWhiteSpace(target.CollectionName))
            return Invalid($"looks into '{target.SchemaName}', which has no collection", target);

        if (rule.LeftSource != ConditionSource.SCHEMA_FIELD)
            return Invalid("compares something other than a field of the record", target);

        var source = SourceField(policySchema, rule.LeftOperand);
        if (source is null)
            return Invalid($"compares '{rule.LeftOperand}', which isn't a field of '{policySchema.SchemaName}'", target);

        var compatibility = ReferenceRuleCompatibility.Check(source, AsDefinition(target), fieldName, policySchema.IsSystemEntity);
        if (compatibility is not null)
            return Invalid($"looks into '{target.SchemaName}.{fieldName}': {compatibility}", target);

        var whereFields = new List<string>();
        if (reference.Filter is not null && FilterProblem(reference.Filter, target, whereFields) is { } filterProblem)
            return Invalid(filterProblem, target);

        if (target.IsSystemEntity && SystemTargetProblem(target, [fieldName, .. whereFields]) is { } systemProblem)
            return Invalid(systemProblem, target);

        return new AccessLookupTarget(target.SchemaName, fieldName, target.CollectionName, ReferenceTargets.StoredFieldName(fieldName), target.IsSystemEntity, null);
    }

    /// <summary>
    /// The live Entity schema named <paramref name="schemaName"/> (case-insensitive, as validation lookups). A policy on
    /// a system schema only sees system schemas, so a tenant schema sharing a system entity's name never answers for it.
    /// </summary>
    private static (SchemaDefinitionExtended? Target, string? Problem) FindTarget(SchemaDefinitionExtended policySchema, IReadOnlyCollection<SchemaDefinitionExtended> candidates, string schemaName)
    {
        if (schemaName.Length == 0)
            return (null, "has no target schema");

        var matches = candidates
            .Prepend(policySchema)
            .Where(s => s is not null && s.SchemaType == SchemaType.Entity && !s.IsDeleted
                        && string.Equals(s.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase)
                        && (!policySchema.IsSystemEntity || s.IsSystemEntity))
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<SchemaDefinitionExtended>()
            .ToList();

        if (matches.Count == 0)
            return (null, $"looks into schema '{schemaName}', which does not exist");
        if (matches.Contains(policySchema))
            return (policySchema, null);
        if (matches.Count > 1)
            return (null, $"looks into schema '{schemaName}', which more than one schema is named");
        return (matches[0], null);
    }

    /// <summary>The left operand's field in the policy schema; <c>ItemId</c> (stored as text) need not be listed.</summary>
    private static FieldDefinition? SourceField(SchemaDefinitionExtended schema, string path)
    {
        if (FindField(schema.Fields, path) is { } field)
            return new FieldDefinition { Name = path, Type = field.Type, IsArray = field.IsArray };
        return path == ReferenceTargets.DefaultTargetField
            ? new FieldDefinition { Name = path, Type = "String" }
            : null;
    }

    private static FieldDefinitionResponse? FindField(List<FieldDefinitionResponse> fields, string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (fields.FirstOrDefault(f => f.Name == path) is { } direct)
            return direct;
        var dot = path.IndexOf('.');
        if (dot < 0)
            return null;
        var parent = fields.FirstOrDefault(f => f.Name == path[..dot]);
        return parent is null ? null : FindField(parent.Fields, path[(dot + 1)..]);
    }

    /// <summary>The target as <see cref="ReferenceRuleCompatibility"/> reads it: its top-level fields.</summary>
    private static SchemaDefinition AsDefinition(SchemaDefinitionExtended target) => new()
    {
        SchemaName = target.SchemaName,
        SchemaType = target.SchemaType,
        IsDeleted = target.IsDeleted,
        IsSystemEntity = target.IsSystemEntity,
        CollectionName = target.CollectionName,
        Fields = target.Fields.Select(f => new FieldDefinition { Name = f.Name, Type = f.Type, IsArray = f.IsArray }).ToList(),
    };

    /// <summary>
    /// Why the WHERE filter can't be run as written, or null. Each condition compares a property of the target with a
    /// fixed value or an Auth value; no lookup inside a lookup. A property the target no longer has would make the
    /// filter match nothing, so NOT_EXISTS_IN would match everyone.
    /// </summary>
    private static string? FilterProblem(PolicyRuleGroup filter, SchemaDefinitionExtended target, List<string> fields)
    {
        foreach (var condition in filter.Rules)
        {
            if (condition.Operator is PolicyOperator.EXISTS_IN or PolicyOperator.NOT_EXISTS_IN)
                return "has a filter that looks into another schema";
            if (condition.LeftSource != ConditionSource.SCHEMA_FIELD || condition.RightSource is not (ConditionSource.STATIC_VALUE or ConditionSource.AUTH))
                return "has a filter that doesn't compare a property with a fixed value or a current-user value";
            if (condition.LeftOperand != ReferenceTargets.DefaultTargetField && !ReferenceTargets.HasProperty(target.Fields, condition.LeftOperand))
                return $"has a filter on '{condition.LeftOperand}', which isn't a property of '{target.SchemaName}'";
            fields.Add(condition.LeftOperand);
        }
        foreach (var nested in filter.NestedGroups)
        {
            if (FilterProblem(nested, target, fields) is { } problem)
                return problem;
        }
        return null;
    }

    /// <summary>
    /// A system entity may be looked into only when it and every field used are open to rules, and its collection is
    /// verified (<see cref="VerifiedSystemCollections"/>).
    /// </summary>
    private static string? SystemTargetProblem(SchemaDefinitionExtended target, IEnumerable<string> fields)
    {
        var rules = SystemSchemaRules.For(target.SchemaName);
        if (rules is null)
            return $"looks into system schema '{target.SchemaName}', which isn't open to rules";
        foreach (var field in fields)
        {
            if (field != ReferenceTargets.DefaultTargetField && rules.ValidationFor(field) == RuleAccess.Blocked)
                return $"uses '{target.SchemaName}.{field}', which isn't open to rules";
        }
        if (!VerifiedSystemCollections.Contains(target.SchemaName))
            return $"looks into system schema '{target.SchemaName}', whose collection isn't verified";
        return null;
    }
}
