using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// The lookup a Reference or Must not exist in rule sends to the target collection: the field is one of the values,
/// and the target row matches the rule's filter and, where deleted rows don't count, isn't deleted. Shared with the
/// Data Gateway, so both query paths send the same lookup.
/// </summary>
public static class ReferenceLookup
{
    private static readonly BsonDocument NotDeleted = new(nameof(GraphQlBaseEntity.IsDeleted), new BsonDocument("$ne", true));

    /// <summary>
    /// Deleted target rows don't count for Must not exist in, nor for Reference rules from evaluation version 2.
    /// Older Reference rules keep today's lookup, where they still count.
    /// </summary>
    public static bool SkipsDeletedRows(ValidationRule rule) =>
        rule.Type == ValidationType.NotExistsIn || rule.EvaluationVersion >= 2;

    /// <summary>
    /// What the lookup adds to "field in values": the filter, with the record's <c>ItemId</c> read as the stored
    /// <c>_id</c>, and the deleted-rows clause. Null when there is neither, so the lookup is exactly today's.
    /// False when the filter can't be built; the rule then fails with a clear message.
    /// </summary>
    public static bool TryCondition(ValidationRule rule, out BsonDocument? condition)
    {
        condition = null;
        var parts = new List<BsonDocument>();
        if (rule.TargetFilter is not null)
        {
            if (ValidationCondition.HasEmptyNestedGroup(rule.TargetFilter))
                return false;
            PolicyEvaluationResult filter;
            try
            {
                filter = DataAccessPolicyHelper.BuildValidationConditionFilter(rule.TargetFilter);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                return false;
            }

            if (!filter.IsAccessGranted)
                parts.Add(MatchesNothing());
            else if (filter.RequiresDataFilter && filter.DataFilter.ElementCount > 0)
                parts.Add(WithStoredId(filter.DataFilter));
        }
        if (SkipsDeletedRows(rule))
            parts.Add(NotDeleted.DeepClone().AsBsonDocument);

        condition = parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => new BsonDocument("$and", new BsonArray(parts)),
        };
        return true;
    }

    /// <summary>The lookup: <paramref name="field"/> is one of <paramref name="values"/>, and the condition when there is one.</summary>
    public static FilterDefinition<BsonDocument> Filter(string field, IEnumerable<BsonValue> values, BsonDocument? condition)
    {
        var lookup = Builders<BsonDocument>.Filter.In(field, values);
        if (condition is null)
            return lookup;

        var inValues = new BsonDocument(field, new BsonDocument("$in", new BsonArray(values)));
        return new BsonDocumentFilterDefinition<BsonDocument>(new BsonDocument("$and", new BsonArray { inValues, condition }));
    }

    // A condition that never holds: no row matches it, so a Reference fails and a Must not exist in passes.
    private static BsonDocument MatchesNothing() =>
        new(PolicyConstants.DbEntityIdFieldName, new BsonDocument("$in", new BsonArray()));

    /// <summary>A copy with the record's own <c>ItemId</c> named <c>_id</c>, at every level of <c>$and</c> / <c>$or</c> / <c>$nor</c>.</summary>
    private static BsonDocument WithStoredId(BsonDocument filter)
    {
        var mapped = new BsonDocument();
        foreach (var element in filter)
        {
            var name = element.Name == nameof(GraphQlBaseEntity.ItemId) ? PolicyConstants.DbEntityIdFieldName : element.Name;
            mapped[name] = element.Name is "$and" or "$or" or "$nor" && element.Value is BsonArray groups
                ? new BsonArray(groups.Select(g => g is BsonDocument group ? WithStoredId(group) : g))
                : element.Value.DeepClone();
        }
        return mapped;
    }
}
