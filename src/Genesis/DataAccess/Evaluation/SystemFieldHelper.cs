using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Maps the entity's <c>ItemId</c> to Mongo's <c>_id</c> inside a filter. Moved here from the gateway's
/// GraphQlTypeHelper because the RLS filter combination needs it; the gateway's extension method
/// forwards to this. Deliberately not an extension method, so the two never collide.
/// </summary>
public static class SystemFieldHelper
{
    public static BsonDocument ReplaceSystemFieldInFilter(BsonDocument filter)
    {
        if (filter.ElementCount > 0 && filter.Contains(nameof(GraphQlBaseEntity.ItemId)))
        {
            //replace ItemId with DbEntityIdFieldName
            filter[PolicyConstants.DbEntityIdFieldName] = filter[nameof(GraphQlBaseEntity.ItemId)];
            filter.Remove(nameof(GraphQlBaseEntity.ItemId));
        }
        return filter;
    }
}
