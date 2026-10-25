using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>Whether a rule's "only when" condition holds on a record.</summary>
public enum ConditionOutcome
{
    Holds,
    DoesNotHold,
    /// <summary>The condition could not be evaluated (for example a pattern that does not parse); the rule fails with a clear message.</summary>
    CannotCheck,
}

/// <summary>
/// Evaluates a validation rule's "only when" condition on a record: the condition becomes a Mongo filter
/// (<see cref="DataAccessPolicyHelper.BuildValidationConditionFilter"/>) and <see cref="BsonFilterMatcher"/> checks it,
/// so dates compare as dates and numbers as numbers, as Mongo would. The record holds typed values.
/// </summary>
public static class ValidationCondition
{
    public static ConditionOutcome Evaluate(PolicyRuleGroup? when, BsonDocument record)
    {
        // An empty group inside another reads as "not granted", which would switch the rule off unnoticed.
        if (when is not null && HasEmptyNestedGroup(when))
            return ConditionOutcome.CannotCheck;
        try
        {
            var condition = DataAccessPolicyHelper.BuildValidationConditionFilter(when);
            if (!condition.IsAccessGranted)
                return ConditionOutcome.DoesNotHold;
            if (!condition.RequiresDataFilter || condition.DataFilter.ElementCount == 0)
                return ConditionOutcome.Holds;
            return BsonFilterMatcher.Matches(record, condition.DataFilter) ? ConditionOutcome.Holds : ConditionOutcome.DoesNotHold;
        }
        // The matcher throws NotSupportedException for filters it can't run, and Regex an ArgumentException for a bad pattern.
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return ConditionOutcome.CannotCheck;
        }
    }

    /// <summary>True when a group nested anywhere in <paramref name="group"/> has no condition.</summary>
    internal static bool HasEmptyNestedGroup(PolicyRuleGroup group) =>
        group.NestedGroups.Any(nested => (nested.Rules.Count == 0 && nested.NestedGroups.Count == 0) || HasEmptyNestedGroup(nested));
}
