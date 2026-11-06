using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Which database-backed Auth values a set of policies reads: <c>Permissions</c> (token plus role-bound
/// permissions) and <c>Attributes.&lt;name&gt;</c>. Used to skip the database reads when no policy needs them.
/// </summary>
public sealed record PolicyAuthUsage(bool UsesPermissions, IReadOnlySet<string> AttributeNames)
{
    public static PolicyAuthUsage None { get; } = new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool IsEmpty => !UsesPermissions && AttributeNames.Count == 0;

    /// <summary>Scans every rule (nested groups included) of <paramref name="policies"/>.</summary>
    public static PolicyAuthUsage Of(IEnumerable<DataAccessPolicy>? policies)
    {
        if (policies is null)
            return None;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in policies)
        {
            if (policy?.RuleGroup is not null)
                CollectNames(policy.RuleGroup, names, depth: 0);
        }

        var usesPermissions = names.Contains("permissions") || names.Contains("permission");
        var attributes = names
            .Where(n => n.StartsWith(DataAccessPolicyHelper.AttributesPrefix, StringComparison.OrdinalIgnoreCase)
                        && n.Length > DataAccessPolicyHelper.AttributesPrefix.Length)
            .Select(n => n[DataAccessPolicyHelper.AttributesPrefix.Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PolicyAuthUsage(usesPermissions, attributes);
    }

    // Policy groups are shallow in practice; the cap only guards against a cyclic object graph.
    private const int MaxDepth = 32;

    private static void CollectNames(PolicyRuleGroup group, HashSet<string> names, int depth)
    {
        if (depth > MaxDepth)
            return;

        foreach (var rule in group.Rules ?? [])
            CollectNames(rule, names);

        foreach (var nested in group.NestedGroups ?? [])
            CollectNames(nested, names, depth + 1);
    }

    private static void CollectNames(PolicyRule rule, HashSet<string> names)
    {
        if (rule.LeftSource == ConditionSource.AUTH && !string.IsNullOrEmpty(rule.LeftOperand))
            names.Add(rule.LeftOperand);

        if (rule.RightSource == ConditionSource.AUTH)
        {
            if (!string.IsNullOrEmpty(rule.RightOperand))
                names.Add(rule.RightOperand);
            foreach (var operand in rule.RightOperands ?? [])
            {
                if (!string.IsNullOrEmpty(operand))
                    names.Add(operand);
            }
        }

        if (rule.StaticValue is string text)
            names.UnionWith(DataAccessPolicyHelper.AuthExpressionNames(text));
    }
}
