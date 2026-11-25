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
        return ReferenceEquals(UsesCache.GetValue(schema, static s => HasLookups(s) ? True : False), True);
    }

    /// <summary>
    /// <see cref="UsesLookups"/> without the cache: for schema instances built per request (DataAccessResolver loads
    /// one per call), which must not leave a cache entry behind. Long-lived schemas use <see cref="UsesLookups"/>.
    /// </summary>
    public static bool HasLookups(SchemaDefinitionExtended schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.Policies.Any(p => p.RuleGroup is not null && Walk(p.RuleGroup).Any());
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

    /// <summary>The EXISTS_IN / NOT_EXISTS_IN rules of a rule group and its nested groups, in order.</summary>
    internal static IEnumerable<PolicyRule> Walk(PolicyRuleGroup group)
    {
        foreach (var rule in group.Rules.Where(r => r.Operator is PolicyOperator.EXISTS_IN or PolicyOperator.NOT_EXISTS_IN))
            yield return rule;
        foreach (var rule in group.NestedGroups.SelectMany(Walk))
            yield return rule;
    }
}

/// <summary>A lookup's WHERE filter for one caller: the Mongo match, or the name of the Auth value the caller lacks.</summary>
public sealed record AccessLookupFilter(BsonDocument Match, string? MissingAuthName);
