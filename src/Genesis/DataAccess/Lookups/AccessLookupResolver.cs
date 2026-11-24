using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Lookups;

/// <summary>
/// Resolves a schema's EXISTS_IN / NOT_EXISTS_IN rules for one request, before the synchronous policy checks run.
/// <list type="bullet">
/// <item>No lookup rule for the requested operations: the same schema instance, no query.</item>
/// <item>Otherwise a per-request copy: each rule group holding a lookup rule is copied once, its lookup rules carry
/// their <see cref="LookupResolution"/>, and every policy that used the group (the column copies derived from a row
/// policy share it) gets a shallow copy pointing at the copied group. Other policies stay the same instances; the
/// given schema is never modified.</item>
/// <item>One query per distinct lookup (collection, field, system flag, filter with the caller's Auth values).
/// The caller's own policies are not applied to it.</item>
/// <item>Fails closed per rule: a missing Auth value, an invalid target or more than <see cref="MaxValues"/> values
/// make the rule never match (both operators), logged. An empty answer makes EXISTS_IN never and NOT_EXISTS_IN always
/// match.</item>
/// </list>
/// </summary>
public sealed class AccessLookupResolver(IAccessLookupRunner runner, ILogger<AccessLookupResolver> logger)
{
    /// <summary>The most values a lookup may find; more and the rule never matches.</summary>
    public const int MaxValues = 5000;

    /// <summary>The query limit: one more than <see cref="MaxValues"/>, to tell "too many" from "exactly that many".</summary>
    public const int QueryLimit = MaxValues + 1;

    private static readonly EventId AuthMissing = new(7101, "AccessLookupAuthMissing");
    private static readonly EventId TargetMissing = new(7102, "AccessLookupTargetMissing");
    private static readonly EventId TooLarge = new(7103, "AccessLookupTooLarge");

    private readonly IAccessLookupRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly ILogger<AccessLookupResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<SchemaDefinitionExtended> ResolveAsync(
        SchemaDefinitionExtended schema,
        AccessPrincipal principal,
        IReadOnlyCollection<PolicyOperation> operations,
        AccessLookupTargets targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(targets);

        if (!AccessLookupPlanner.UsesLookups(schema))
            return schema;
        var rules = AccessLookupPlanner.RulesFor(schema, operations);
        if (rules.Count == 0)
            return schema;

        var resolutions = new Dictionary<PolicyRule, LookupResolution>(ReferenceEqualityComparer.Instance);
        var answers = new Dictionary<(string, string, bool, string), IReadOnlyList<BsonValue>?>();
        foreach (var rule in rules)
        {
            if (!resolutions.ContainsKey(rule))
                resolutions[rule] = await ResolveRuleAsync(schema, rule, principal, targets, answers, cancellationToken).ConfigureAwait(false);
        }

        var groupCopies = new Dictionary<PolicyRuleGroup, PolicyRuleGroup?>(ReferenceEqualityComparer.Instance);
        var policies = new List<DataAccessPolicy>(schema.Policies.Count);
        foreach (var policy in schema.Policies)
        {
            var group = policy.RuleGroup is null ? null : CopyIfResolved(policy.RuleGroup, resolutions, groupCopies);
            policies.Add(group is null ? policy : policy.WithRuleGroup(group));
        }
        return schema.WithPolicies(policies);
    }

    private async Task<LookupResolution> ResolveRuleAsync(
        SchemaDefinitionExtended schema,
        PolicyRule rule,
        AccessPrincipal principal,
        AccessLookupTargets targets,
        Dictionary<(string, string, bool, string), IReadOnlyList<BsonValue>?> answers,
        CancellationToken cancellationToken)
    {
        var reference = rule.Reference;
        var target = reference is null ? null : targets.Find(reference);
        if (target is null || target.InvalidReason is not null)
        {
            _logger.LogWarning(TargetMissing,
                "Access lookup on {Schema} gives no access: target {Target}.{Field} {Reason}",
                schema.SchemaName, target?.SchemaName ?? reference?.SchemaName, target?.FieldName ?? reference?.FieldName,
                target?.InvalidReason ?? "was not checked for this schema");
            return LookupResolution.Never;
        }

        AccessLookupFilter filter;
        try
        {
            filter = DataAccessPolicyHelper.BuildAccessLookupFilter(reference!.Filter, principal);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(TargetMissing,
                "Access lookup on {Schema} gives no access: target {Target}.{Field} has a filter that can't be run ({Reason})",
                schema.SchemaName, target.SchemaName, target.FieldName, ex.Message);
            return LookupResolution.Never;
        }

        // Missing-Auth conditions are left out of the match, which would then be too wide.
        if (filter.MissingAuthName is not null)
        {
            _logger.LogInformation(AuthMissing,
                "Access lookup on {Schema} gives no access: the caller has no {AuthName} value for target {Target}.{Field}",
                schema.SchemaName, filter.MissingAuthName, target.SchemaName, target.FieldName);
            return LookupResolution.Never;
        }

        var match = Match(filter.Match, target.DbFieldName);
        var key = (target.CollectionName, target.DbFieldName, target.IsSystemEntity, match.ToJson());
        if (!answers.TryGetValue(key, out var values))
        {
            var found = await _runner.RunAsync(new AccessLookupQuery(target.CollectionName, target.DbFieldName, target.IsSystemEntity, match, QueryLimit), cancellationToken).ConfigureAwait(false);
            if (found.Count > MaxValues)
            {
                _logger.LogWarning(TooLarge,
                    "Access lookup on {Schema} gives no access: target {Target}.{Field} matched {MatchCount} values, more than {MaxValues}",
                    schema.SchemaName, target.SchemaName, target.FieldName, found.Count, MaxValues);
                values = null;
            }
            else
            {
                // {$in: [null]} would match rows without the field.
                values = found.Where(v => v is not (null or BsonNull or BsonUndefined)).ToList();
            }
            answers[key] = values;
        }

        if (values is null)
            return LookupResolution.Never;
        if (values.Count == 0)
            return rule.Operator == PolicyOperator.NOT_EXISTS_IN ? LookupResolution.Always : LookupResolution.Never;
        return LookupResolution.Of(values);
    }

    /// <summary>The WHERE filter, the field present, and the row not deleted.</summary>
    private static BsonDocument Match(BsonDocument where, string dbFieldName)
    {
        var parts = new BsonArray();
        if (where.ElementCount > 0)
            parts.Add(where);
        parts.Add(new BsonDocument(dbFieldName, new BsonDocument("$ne", BsonNull.Value)));
        parts.Add(new BsonDocument(nameof(GraphQlBaseEntity.IsDeleted), new BsonDocument("$ne", true)));
        return new BsonDocument("$and", parts);
    }

    /// <summary>
    /// A copy of <paramref name="group"/> whose resolved lookup rules carry their resolution, or null when nothing in
    /// it was resolved. Each group is copied once, so policies sharing a group share its copy.
    /// </summary>
    private static PolicyRuleGroup? CopyIfResolved(PolicyRuleGroup group, Dictionary<PolicyRule, LookupResolution> resolutions, Dictionary<PolicyRuleGroup, PolicyRuleGroup?> copies)
    {
        if (copies.TryGetValue(group, out var known))
            return known;

        var nested = group.NestedGroups.Select(g => (Original: g, Copy: CopyIfResolved(g, resolutions, copies))).ToList();
        var changed = nested.Any(n => n.Copy is not null) || group.Rules.Any(resolutions.ContainsKey);
        var copy = changed
            ? new PolicyRuleGroup
            {
                LogicalOperator = group.LogicalOperator,
                Rules = group.Rules.Select(r => resolutions.TryGetValue(r, out var resolution) ? r.WithResolution(resolution) : r).ToList(),
                NestedGroups = nested.Select(n => n.Copy ?? n.Original).ToList(),
            }
            : null;
        copies[group] = copy;
        return copy;
    }
}
