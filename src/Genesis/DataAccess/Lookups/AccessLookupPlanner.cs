using System.Runtime.CompilerServices;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Lookups;

/// <summary>
/// Finds the cross-schema lookup rules (<see cref="PolicyOperator.EXISTS_IN"/> / <see cref="PolicyOperator.NOT_EXISTS_IN"/>)
/// of a schema's access policies.
/// </summary>
public static class AccessLookupPlanner
{
    private static readonly ConditionalWeakTable<SchemaDefinitionExtended, object> UsesCache = new();
    private static readonly object True = new();
    private static readonly object False = new();

    /// <summary>True when any policy of the schema, at any group depth, has a lookup rule. Cached per schema instance.</summary>
    public static bool UsesLookups(SchemaDefinitionExtended schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return ReferenceEquals(UsesCache.GetValue(schema, s => s.Policies.Any(p => p.RuleGroup is not null && Walk(p.RuleGroup).Any()) ? True : False), True);
    }

    /// <summary>The lookup rules of the policies whose operation is in <paramref name="operations"/> or ALL, in document order.</summary>
    public static IReadOnlyList<PolicyRule> RulesFor(SchemaDefinitionExtended schema, IReadOnlyCollection<PolicyOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(operations);
        return schema.Policies
            .Where(p => p.RuleGroup is not null && (p.Operation == PolicyOperation.ALL || operations.Contains(p.Operation)))
            .SelectMany(p => Walk(p.RuleGroup))
            .ToList();
    }

    private static IEnumerable<PolicyRule> Walk(PolicyRuleGroup group)
    {
        foreach (var rule in group.Rules)
            if (rule.Operator is PolicyOperator.EXISTS_IN or PolicyOperator.NOT_EXISTS_IN)
                yield return rule;
        foreach (var nested in group.NestedGroups)
            foreach (var rule in Walk(nested))
                yield return rule;
    }
}

/// <summary>A lookup's WHERE filter for one caller: the Mongo match, or the name of the Auth value the caller lacks.</summary>
public sealed record AccessLookupFilter(BsonDocument Match, string? MissingAuthName);
