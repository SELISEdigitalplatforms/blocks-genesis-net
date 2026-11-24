using System.Collections.Concurrent;
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
/// <item>Lookup rules in deny policies are never resolved or queried: "never matches" would lift the deny. They stay
/// unresolved, so evaluation refuses the request (fails closed).</item>
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

    /// <summary>The most distinct target problems remembered; past it every one is logged again (never fewer logs).</summary>
    private const int MaxTargetMissingRemembered = 10_000;

    // AccessLookupTargetMissing is a configuration problem that repeats on every request: logged once per
    // (schema, target, reason) for the life of this resolver (a singleton, so once per process).
    private readonly ConcurrentDictionary<(string?, string?, string?, string), byte> _targetMissingLogged = new();

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

        // Callers check first (the gateway with the cached UsesLookups on its long-lived schemas, DataAccessResolver
        // with HasLookups on its per-request ones); this walk caches nothing, so no per-request entry is left behind.
        if (!AccessLookupPlanner.HasLookups(schema))
            return schema;
        // Deny policies are never resolved: a rule that never matches would lift the deny. Left unresolved, they
        // reach the evaluation backstop, which refuses the request.
        var rules = AccessLookupPlanner.RulesFor(schema.WithPolicies(schema.Policies.Where(p => p.IsAllowPolicy).ToList()), operations);
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
            var group = policy.IsAllowPolicy && policy.RuleGroup is not null ? CopyIfResolved(policy.RuleGroup, resolutions, groupCopies) : null;
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
        var target = reference is null ? null : targets.Find(schema, rule);
        if (target is null || target.InvalidReason is not null)
        {
            var (targetName, fieldName) = (target?.SchemaName ?? reference?.SchemaName, target?.FieldName ?? reference?.FieldName);
            var reason = target?.InvalidReason ?? "was not checked for this schema";
            if (FirstTargetMissing(schema.SchemaName, targetName, fieldName, reason))
                _logger.LogWarning(TargetMissing,
                    "Access lookup on {Schema} gives no access: target {Target}.{Field} {Reason}",
                    schema.SchemaName, targetName, fieldName, reason);
            return LookupResolution.Never;
        }

        AccessLookupFilter filter;
        try
        {
            filter = DataAccessPolicyHelper.BuildAccessLookupFilter(reference!.Filter, principal);
        }
        catch (InvalidOperationException ex)
        {
            if (FirstTargetMissing(schema.SchemaName, target.SchemaName, target.FieldName, ex.Message))
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
            var found = await _runner.RunAsync(new AccessLookupQuery(target.CollectionName, target.DbFieldName, target.IsSystemEntity, match, QueryLimit), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Access lookup runner returned no result.");
            if (found.Count > MaxValues)
            {
                // The query stops at QueryLimit, so the real count is only known to be above MaxValues.
                _logger.LogWarning(TooLarge,
                    "Access lookup on {Schema} gives no access: target {Target}.{Field} matched more than {MaxValues} values",
                    schema.SchemaName, target.SchemaName, target.FieldName, MaxValues);
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

    /// <summary>True the first time a (schema, target, reason) is seen, and always once the memory is full.</summary>
    private bool FirstTargetMissing(string? schemaName, string? targetName, string? fieldName, string reason)
    {
        var key = (schemaName, targetName, fieldName, reason);
        if (_targetMissingLogged.ContainsKey(key))
            return false;
        return _targetMissingLogged.Count >= MaxTargetMissingRemembered || _targetMissingLogged.TryAdd(key, 0);
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
