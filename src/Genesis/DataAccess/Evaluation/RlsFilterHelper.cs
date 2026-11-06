using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Combines a caller's filter with an RLS data filter. The two methods keep the two slightly different
/// combinations the gateway used for reads and for mutations.
/// </summary>
public static class RlsFilterHelper
{
    /// <summary>Mutation combination (former MutationFilterHelper.ApplyPolicyFilter): base AND rls when a data filter is required.</summary>
    public static BsonDocument ApplyPolicyFilter(BsonDocument baseFilter, PolicyEvaluationResult rlsResult)
    {
        if (!rlsResult.RequiresDataFilter || rlsResult.DataFilter.ElementCount == 0)
            return baseFilter;
        return new BsonDocument("$and", new BsonArray { baseFilter, rlsResult.DataFilter });
    }

    /// <summary>Read combination (former QueryService.BuildFilterWithRlsPolicy): the RLS filter alone when the user filter is empty, else user AND rls.</summary>
    public static BsonDocument CombineUserFilterWithRls(BsonDocument userFilterBson, PolicyEvaluationResult rlsResult)
    {
        var rlsFilter = rlsResult.RequiresDataFilter ? rlsResult.DataFilter : new BsonDocument();
        var defaultFilter = new BsonDocument();

        if (userFilterBson.ElementCount == 0)
            return rlsFilter.ElementCount == 0 ? defaultFilter : rlsFilter;

        var userFilter = new BsonDocument(userFilterBson);
        SystemFieldHelper.ReplaceSystemFieldInFilter(userFilter);
        if (rlsFilter.ElementCount > 0)
            userFilter = new BsonDocument("$and", new BsonArray { userFilter, rlsFilter });
        return userFilter;
    }
}
