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
    /// A <see cref="RowSatisfiesPolicy"/> that evaluates each rule group once for a row. Every
    /// field that inherits its access gets its own copy of each row-level policy, and the copies
    /// share the source policy's rule group, so a schema with many such fields would otherwise
    /// evaluate the same rules once per field for every row. The answer depends only on the
    /// principal, the rule group and the row, so it is reused for the row and recomputed when
    /// another row is passed.
    /// </summary>
    public static Func<DataAccessPolicy, IDictionary<string, object>, bool> OncePerRuleGroup(AccessPrincipal principal)
    {
        IDictionary<string, object>? currentRow = null;
        var results = new Dictionary<PolicyRuleGroup, bool>(ReferenceEqualityComparer.Instance);

        return (policy, rowData) =>
        {
            if (policy.RuleGroup == null) return false;

            if (!ReferenceEquals(currentRow, rowData))
            {
                results.Clear();
                currentRow = rowData;
            }

            if (!results.TryGetValue(policy.RuleGroup, out var satisfied))
            {
                satisfied = EvaluateRuleGroupForRow(principal, policy.RuleGroup, rowData);
                results[policy.RuleGroup] = satisfied;
            }
            return satisfied;
        };
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
        var rightOperands = rule.RightOperands.Count > 0 ? rule.RightOperands : [rule.RightOperand];
        var results = rightOperands.Select(operand => EvaluateOperandPairForRow(principal, rule, operand, rowData));
        return rule.Operator is PolicyOperator.NOT_IN or PolicyOperator.NOT_CONTAIN
            ? results.All(result => result)
            : results.Any(result => result);
    }

    /// <summary>
    /// Evaluates the rule's left operand against one right operand. When exactly one side is a schema field whose
    /// path runs through a list (<c>Signatories.UserId</c> where <c>Signatories</c> is a list of objects), the
    /// comparison is made against every item, with the meaning the read filter gives it in MongoDB: see
    /// <see cref="EvaluateAcrossItems"/>. Otherwise the two resolved values are compared once.
    /// </summary>
    private static bool EvaluateOperandPairForRow(AccessPrincipal principal, PolicyRule rule, string rightOperand, IDictionary<string, object> rowData)
    {
        var leftIsField = rule.LeftSource == ConditionSource.SCHEMA_FIELD;
        var rightIsField = rule.RightSource == ConditionSource.SCHEMA_FIELD;

        // Is null / Is not null test the left operand only, so a list on the right changes nothing. A field compared
        // with a field is a $expr in the filter, which compares whole values; it keeps the single comparison.
        var unaryOperator = rule.Operator is PolicyOperator.IS_NULL or PolicyOperator.IS_NOT_NULL;
        if (leftIsField != rightIsField && !(unaryOperator && rightIsField))
        {
            var fieldPath = leftIsField ? rule.LeftOperand : rightOperand;
            if (TryGetValuesThroughLists(rowData, fieldPath, out var items))
            {
                var other = leftIsField
                    ? ResolveRuleOperandValue(principal, rule.RightSource, rightOperand, rule.StaticValue, rowData)
                    : ResolveRuleOperandValue(principal, rule.LeftSource, rule.LeftOperand, rule.StaticValue, rowData);
                return EvaluateAcrossItems(rule.Operator, items, (item, op) => leftIsField
                    ? DataAccessPolicyHelper.EvaluateCondition(item, op, other)
                    : DataAccessPolicyHelper.EvaluateCondition(other, op, item));
            }
        }

        return DataAccessPolicyHelper.EvaluateCondition(
            ResolveRuleOperandValue(principal, rule.LeftSource, rule.LeftOperand, rule.StaticValue, rowData),
            rule.Operator,
            ResolveRuleOperandValue(principal, rule.RightSource, rightOperand, rule.StaticValue, rowData));
    }

    /// <summary>
    /// A comparison against a path that runs through a list, as MongoDB answers the filter built from the same rule
    /// (and as <see cref="BsonFilterMatcher"/> does): a positive comparison holds when it holds for any item; Not
    /// equal, Not in and Not contain (<c>$ne</c>, <c>$nin</c>, <c>$not</c>) hold when the positive comparison holds
    /// for no item; Is null holds when an item has no value there, and Is not null when every item has one.
    /// </summary>
    private static bool EvaluateAcrossItems(PolicyOperator op, List<object?> items, Func<object?, PolicyOperator, bool> holds)
    {
        bool AnyHolds(PolicyOperator positive) => items.Any(item => holds(item, positive));
        bool AnyIsNull() => items.Count == 0 || AnyHolds(PolicyOperator.IS_NULL);

        return op switch
        {
            PolicyOperator.IS_NULL => AnyIsNull(),
            PolicyOperator.IS_NOT_NULL => !AnyIsNull(),
            PolicyOperator.NOT_EQUAL => !AnyHolds(PolicyOperator.EQUAL),
            PolicyOperator.NOT_IN => !AnyHolds(PolicyOperator.IN),
            PolicyOperator.NOT_CONTAIN => !AnyHolds(PolicyOperator.CONTAIN),
            _ => AnyHolds(op)
        };
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

    // Each arm is cast to object: BsonValue converts implicitly from string, int, bool and the rest, so without the
    // casts the switch would be typed BsonValue and hand back a BsonString again.
    private static object? FromBson(BsonValue value) => value.BsonType switch
    {
        BsonType.Null => null,
        BsonType.String => (object)value.AsString,
        BsonType.Int32 => (object)value.AsInt32,
        BsonType.Int64 => (object)value.AsInt64,
        BsonType.Double => (object)value.AsDouble,
        BsonType.Boolean => (object)value.AsBoolean,
        BsonType.DateTime => (object)value.ToUniversalTime(),
        _ => (object)value
    };

    /// <summary>
    /// The values at a dotted path when the path runs through a list, read the way MongoDB reads a path in a filter:
    /// a list of objects is entered item by item (<c>Signatories.UserId</c> gives each signatory's UserId), a number
    /// segment also picks that item (<c>Signatories.0.UserId</c>), and an item that has no value there gives null.
    /// Returns false, with no values, when the path does not run through a list; such a path keeps its single value
    /// from <see cref="GetNestedValueByPath"/>. A list at the end of the path is a value, not a list to enter.
    /// </summary>
    public static bool TryGetValuesThroughLists(IDictionary<string, object> rowData, string fieldPath, out List<object?> values)
    {
        values = [];
        if (string.IsNullOrEmpty(fieldPath) || !fieldPath.Contains('.'))
            return false;

        var throughList = false;
        CollectValues(rowData, fieldPath.Split('.'), 0, inList: false, values, ref throughList);
        if (!throughList)
            values.Clear();
        return throughList;
    }

    private static void CollectValues(object? current, string[] segments, int index, bool inList, List<object?> output, ref bool throughList)
    {
        if (index == segments.Length)
        {
            output.Add(current is BsonValue bson ? FromBson(bson) : current);
            return;
        }

        var segment = segments[index];
        switch (current)
        {
            case null:
            case BsonNull:
                if (inList) output.Add(null);
                return;
            case IDictionary<string, object> map:
                if (map.TryGetValue(segment, out var next))
                    CollectValues(next, segments, index + 1, inList, output, ref throughList);
                else if (inList)
                    output.Add(null);
                return;
            case BsonDocument document:
                if (document.TryGetValue(segment, out var nextBson))
                    CollectValues(nextBson, segments, index + 1, inList, output, ref throughList);
                else if (inList)
                    output.Add(null);
                return;
            case string:
                return;
            case System.Collections.IEnumerable list:
                throughList = true;
                var items = list.Cast<object?>().ToList();
                if (int.TryParse(segment, out var position))
                {
                    if (position >= 0 && position < items.Count)
                        CollectValues(items[position], segments, index + 1, inList: true, output, ref throughList);
                    return;
                }
                foreach (var item in items.Where(item => item is IDictionary<string, object> or BsonDocument))
                    CollectValues(item, segments, index, inList: true, output, ref throughList);
                return;
        }
    }
}
