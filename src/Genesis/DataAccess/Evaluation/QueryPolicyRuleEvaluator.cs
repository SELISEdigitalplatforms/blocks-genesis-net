using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Evaluates policy rule groups against a single row of data (for CLS row-level evaluation).
/// Used to determine which fields are allowed for a row based on policy conditions.
/// </summary>
public static class QueryPolicyRuleEvaluator
{
    /// <summary>
    /// Returns true if the row satisfies the policy's rule group.
    /// </summary>
    public static bool RowSatisfiesPolicy(AccessPrincipal principal, DataAccessPolicy policy, IDictionary<string, object> rowData)
    {
        if (policy.RuleGroup == null) return false;
        return EvaluateRuleGroupForRow(principal, policy.RuleGroup, rowData);
    }

    /// <summary>
    /// Evaluates a rule group against row data (AND/OR of rules and nested groups).
    /// </summary>
    public static bool EvaluateRuleGroupForRow(AccessPrincipal principal, PolicyRuleGroup group, IDictionary<string, object> rowData)
    {
        var ruleResults = group.Rules.Select(rule => EvaluateSingleRuleForRow(principal, rule, rowData)).ToList();
        var nestedResults = group.NestedGroups.Select(nested => EvaluateRuleGroupForRow(principal, nested, rowData)).ToList();
        var allResults = ruleResults.Concat(nestedResults).ToList();

        if (allResults.Count == 0) return false;
        return group.LogicalOperator == PolicyLogicalOperator.AND
            ? allResults.All(r => r)
            : allResults.Any(r => r);
    }

    /// <summary>
    /// Evaluates a single rule by resolving operands and comparing via DataAccessPolicyHelper.
    /// </summary>
    public static bool EvaluateSingleRuleForRow(AccessPrincipal principal, PolicyRule rule, IDictionary<string, object> rowData)
    {
        var leftValue = ResolveRuleOperandValue(principal, rule.LeftSource, rule.LeftOperand, rule.StaticValue, rowData);
        var rightOperands = rule.RightOperands.Count > 0 ? rule.RightOperands : [rule.RightOperand];
        var results = rightOperands.Select(operand => DataAccessPolicyHelper.EvaluateCondition(
            leftValue,
            rule.Operator,
            ResolveRuleOperandValue(principal, rule.RightSource, operand, rule.StaticValue, rowData)));
        return rule.Operator is PolicyOperator.NOT_IN or PolicyOperator.NOT_CONTAIN
            ? results.All(result => result)
            : results.Any(result => result);
    }

    /// <summary>
    /// Resolves an operand value from AUTH token, schema field, or static value.
    /// </summary>
    public static object? ResolveRuleOperandValue(
        AccessPrincipal principal,
        ConditionSource source,
        string operand,
        object? staticValue,
        IDictionary<string, object> rowData)
    {
        return source switch
        {
            ConditionSource.AUTH => DataAccessPolicyHelper.GetTokenValue(principal, operand),
            ConditionSource.SCHEMA_FIELD => GetNestedValueByPath(rowData, operand),
            ConditionSource.STATIC_VALUE => staticValue,
            _ => null
        };
    }

    /// <summary>
    /// Gets a nested value from the row by dotted path (e.g. "ContactInfo.Email").
    /// </summary>
    public static object? GetNestedValueByPath(IDictionary<string, object> rowData, string fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath)) return null;

        var dotIndex = fieldPath.IndexOf('.');
        if (dotIndex < 0)
            return rowData.TryGetValue(fieldPath, out var value) ? value : null;

        var segment = fieldPath[..dotIndex];
        var restPath = fieldPath[(dotIndex + 1)..];
        if (!rowData.TryGetValue(segment, out var segmentValue) || segmentValue == null)
            return null;

        return segmentValue switch
        {
            IDictionary<string, object> nested => GetNestedValueByPath(nested, restPath),
            BsonDocument bson => GetNestedValueFromBson(bson, restPath),
            _ => null
        };
    }

    /// <summary>
    /// Gets a nested value from a BsonDocument by dotted path.
    /// </summary>
    public static object? GetNestedValueFromBson(BsonDocument doc, string path)
    {
        var dotIndex = path.IndexOf('.');
        var segment = dotIndex < 0 ? path : path[..dotIndex];
        if (!doc.Contains(segment))
            return null;

        var value = doc[segment];
        if (value == null || value.IsBsonNull)
            return null;

        if (dotIndex < 0)
            return value.BsonType switch
            {
                BsonType.String => value.AsString,
                BsonType.Int32 => value.AsInt32,
                BsonType.Int64 => value.AsInt64,
                BsonType.Double => value.AsDouble,
                BsonType.Boolean => value.AsBoolean,
                BsonType.DateTime => value.ToUniversalTime(),
                BsonType.Document => value.AsBsonDocument,
                BsonType.Array => value.AsBsonArray,
                _ => value
            };

        if (value.IsBsonDocument)
            return GetNestedValueFromBson(value.AsBsonDocument, path[(dotIndex + 1)..]);
        return null;
    }
}
