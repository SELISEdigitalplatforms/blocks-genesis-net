// Moved from blocks-data server/DataGateway.DomainService/Helpers/ReferenceTargets.cs (dev 3664eb77) so the gateway and the platform services share one rules engine.
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>Where a Reference rule points: the target schema's collection and the stored name of the target field.</summary>
public sealed record ReferenceTarget(string SchemaName, string FieldName, string CollectionName, string DbFieldName);

/// <summary>
/// Resolves Reference rules (value must exist in another schema's data) against the schemas of one build.
/// A mutation only sees its own schema, so the target's collection is looked up here, once per build,
/// from every schema in the snapshot.
/// </summary>
public sealed class ReferenceTargets
{
    public const string DefaultTargetField = nameof(GraphQlBaseEntity.ItemId);

    public static readonly ReferenceTargets Empty = new(new Dictionary<(string, string), ReferenceTarget>(), []);

    private readonly Dictionary<(string Schema, string Field), ReferenceTarget> _targets;
    private readonly HashSet<string> _sourceSchemaIds;

    private ReferenceTargets(Dictionary<(string, string), ReferenceTarget> targets, HashSet<string> sourceSchemaIds)
    {
        _targets = targets;
        _sourceSchemaIds = sourceSchemaIds;
    }

    /// <summary>Whether any field of <paramref name="schema"/> carries an active, resolved Reference rule.</summary>
    public bool HasReferences(SchemaDefinitionExtended schema) => _sourceSchemaIds.Contains(schema.ItemId);

    /// <summary>The target of an active Reference rule, or null when it could not be resolved at build time.</summary>
    public ReferenceTarget? Find(ValidationRule rule) =>
        _targets.GetValueOrDefault((TargetSchemaName(rule), TargetFieldName(rule)));

    public static string TargetSchemaName(ValidationRule rule) => rule.Value?.ToString()?.Trim() ?? string.Empty;

    public static string TargetFieldName(ValidationRule rule)
    {
        var field = rule.SecondaryValue?.ToString()?.Trim();
        return string.IsNullOrEmpty(field) ? DefaultTargetField : field;
    }

    /// <summary>
    /// Resolves every active Reference rule in <paramref name="schemas"/>. A rule whose target schema or field
    /// is missing throws when <paramref name="strict"/> (a publish, so the admin sees why), and is otherwise
    /// logged and left unchecked (a pod rebuilding a version that was already published).
    /// </summary>
    public static ReferenceTargets Build(IReadOnlyCollection<SchemaDefinitionExtended> schemas, bool strict, ILogger logger)
    {
        var entities = schemas
            .Where(s => s.SchemaType == SchemaType.Entity)
            .GroupBy(s => s.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var targets = new Dictionary<(string, string), ReferenceTarget>();
        var sourceSchemaIds = new HashSet<string>();
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

        return targets.Count == 0 ? Empty : new ReferenceTargets(targets, sourceSchemaIds);
    }

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
            target = new ReferenceTarget(targetSchema.SchemaName, fieldName, targetSchema.CollectionName, PolicyConstants.DbEntityIdFieldName);
            return null;
        }

        var field = targetSchema.Fields.FirstOrDefault(f => f.Name == fieldName);
        if (field is null || field.IsArray || !SchemaTypeHelper.IsScalar(field.Type))
            return $"references field '{targetSchema.SchemaName}.{fieldName}', which does not exist or is not a single value";

        target = new ReferenceTarget(targetSchema.SchemaName, fieldName, targetSchema.CollectionName, fieldName);
        return null;
    }

    /// <summary>Every active Reference rule under <paramref name="fields"/>, with its dotted path.</summary>
    private static IEnumerable<(string Path, ValidationRule Rule)> ReferenceRules(List<FieldDefinitionResponse> fields, string prefix)
    {
        foreach (var field in fields)
        {
            var path = $"{prefix}{field.Name}";
            foreach (var rule in field.ValidationRule?.Validations ?? [])
            {
                if (rule.IsActive && rule.Type == ValidationType.Reference)
                    yield return (path, rule);
            }
            foreach (var nested in ReferenceRules(field.Fields, $"{path}."))
                yield return nested;
        }
    }
}
