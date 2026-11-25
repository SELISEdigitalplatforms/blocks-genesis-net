using System.Text.RegularExpressions;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Lookups;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// Save-time checks for EXISTS_IN / NOT_EXISTS_IN rules, shared by every path that saves a policy. Pure: no database.
/// Policies without these operators never produce a message.
/// </summary>
public static class PolicyLookupRuleCheck
{
    public const string AllowOnlyMessage = "Exists in another schema is only available in rule sets that allow access.";
    public const string NotAvailableMessage = "Exists in another schema isn't available yet on this platform.";
    public const string SystemOnlyMessage = AccessLookupTargets.SystemOnlyMessage;
    public const string NestedLookupMessage = "Exists in another schema can't be used inside a lookup's WHERE box.";
    public const string TooDeepMessage = "A lookup's WHERE box can be nested at most 3 levels deep.";
    public const string IncludesMessage = "Use 'includes' to compare a list with the current user.";

    private const int MaxWhereDepth = 3;

    /// <summary>The Auth names a rule may read (lower-case, matched case-insensitively).</summary>
    public static IReadOnlySet<string> AcceptedAuthNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "userid", "id", "sub", "email", "tenantid", "tenant", "roles", "role",
        "permissions", "permission", "organizationid", "organization",
    };

    private static bool IsLookup(PolicyRule rule) => rule.Operator is PolicyOperator.EXISTS_IN or PolicyOperator.NOT_EXISTS_IN;

    /// <summary>The lookup rules of the policy's own rule group (not those inside a WHERE box).</summary>
    private static IEnumerable<PolicyRule> LookupRules(PolicyRuleGroup? group)
    {
        if (group is null)
            yield break;
        foreach (var rule in group.Rules ?? [])
            if (rule is not null && IsLookup(rule))
                yield return rule;
        foreach (var nested in group.NestedGroups ?? [])
            foreach (var rule in LookupRules(nested))
                yield return rule;
    }

    private static IEnumerable<PolicyRule> AllRules(PolicyRuleGroup? group)
    {
        if (group is null)
            yield break;
        foreach (var rule in group.Rules ?? [])
            if (rule is not null)
                yield return rule;
        foreach (var nested in group.NestedGroups ?? [])
            foreach (var rule in AllRules(nested))
                yield return rule;
    }

    /// <summary>What is wrong with the shape of the policy's lookup rules; empty when fine or when it has none.</summary>
    public static IReadOnlyList<string> Shape(DataAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var messages = new List<string>();
        var rules = AllRules(policy.RuleGroup).ToList();

        foreach (var rule in rules.Where(r => !IsLookup(r) && r.Reference is not null))
            messages.Add($"A rule on '{rule.LeftOperand}' carries a lookup target, but only exists-in rules can.");

        var lookups = rules.Where(IsLookup).ToList();
        if (lookups.Count == 0)
            return messages;

        if (!policy.IsAllowPolicy)
            messages.Add(AllowOnlyMessage);

        foreach (var rule in lookups)
        {
            if (rule.Reference is null)
            {
                messages.Add($"The exists-in rule on '{rule.LeftOperand}' has no schema to look into.");
                continue;
            }
            if (rule.Reference.Filter is { } filter)
                CheckWhere(filter, 1, messages);
        }
        return messages.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void CheckWhere(PolicyRuleGroup group, int depth, List<string> messages)
    {
        if (depth > MaxWhereDepth)
        {
            messages.Add(TooDeepMessage);
            return;
        }
        foreach (var condition in group.Rules ?? [])
        {
            if (condition is null)
                continue;
            if (IsLookup(condition))
            {
                messages.Add(NestedLookupMessage);
                continue;
            }
            if (condition.LeftSource != ConditionSource.SCHEMA_FIELD)
                messages.Add($"The left side of a lookup's WHERE condition on '{condition.LeftOperand}' must be a field of the schema looked into.");
            CheckWhereRight(condition, messages);
            if (condition.Reference is not null)
                messages.Add($"A WHERE condition on '{condition.LeftOperand}' carries a lookup target, but only exists-in rules can.");
        }
        foreach (var nested in group.NestedGroups ?? [])
            CheckWhere(nested, depth + 1, messages);
    }

    private static bool IsPatternOperator(PolicyOperator op) =>
        op is PolicyOperator.CONTAIN or PolicyOperator.NOT_CONTAIN or PolicyOperator.REGEX;

    private static void CheckWhereRight(PolicyRule condition, List<string> messages)
    {
        switch (condition.RightSource)
        {
            case ConditionSource.AUTH:
                if (!AcceptedAuthNames.Contains((condition.RightOperand ?? string.Empty).Trim()))
                    messages.Add($"'{condition.RightOperand}' isn't a value of the current user a rule can use.");
                // Contains / Regex read the caller's value as a pattern: "bob@x.com" would also match "jimbob@x.com".
                if (IsPatternOperator(condition.Operator))
                    messages.Add(IncludesMessage);
                break;
            case ConditionSource.STATIC_VALUE:
                // Every {{auth.X}} / {{token.X}} in the text, wherever it is, as the lookup filter reads it.
                var authNames = condition.StaticValue is string text ? DataAccessPolicyHelper.AuthExpressionNames(text).ToList() : [];
                foreach (var name in authNames.Where(name => !AcceptedAuthNames.Contains(name)))
                    messages.Add($"'{name}' isn't a value of the current user a rule can use.");
                if (authNames.Count > 0 && IsPatternOperator(condition.Operator))
                    messages.Add(IncludesMessage);
                else if (authNames.Count == 0 && IsPatternOperator(condition.Operator)
                         && condition.StaticValue is not null && !CompilesAsRegex(condition.StaticValue.ToString() ?? string.Empty))
                    messages.Add($"This pattern isn't valid: {condition.StaticValue}");
                break;
            default:
                messages.Add($"The right side of a lookup's WHERE condition on '{condition.LeftOperand}' must be a fixed value or a value of the current user.");
                break;
        }
    }

    private static bool CompilesAsRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Every reason a lookup rule of the policy no longer fits its target.</summary>
    public static IReadOnlyList<string> Targets(DataAccessPolicy policy, SchemaDefinitionExtended policySchema, AccessLookupTargets targets)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policySchema);
        ArgumentNullException.ThrowIfNull(targets);
        var messages = new List<string>();
        foreach (var rule in LookupRules(policy.RuleGroup).Where(r => r.Reference is not null))
        {
            var target = targets.Find(policySchema, rule);
            if (target is null)
                messages.Add($"{rule.Reference!.SchemaName}.{rule.Reference.FieldName} can't be found.");
            else if (target.InvalidReason is not null)
                messages.Add(target.InvalidReason);
        }
        return messages.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The refusal when the platform's level doesn't allow the policy's lookup rules; null when allowed or none.</summary>
    public static string? Level(DataAccessPolicy policy, bool policyOnSystemSchema, CrossSchemaAccessLevel level)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!LookupRules(policy.RuleGroup).Any())
            return null;
        return level switch
        {
            CrossSchemaAccessLevel.AllSchemas => null,
            CrossSchemaAccessLevel.TenantSchemas when !policyOnSystemSchema => null,
            _ => NotAvailableMessage,
        };
    }
}
