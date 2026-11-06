using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// The two gateway constants the policy evaluation needs. Copied from the gateway's GraphQlConstant
/// so this assembly stays free of GraphQL concerns; the gateway aliases these to avoid drift.
/// </summary>
public static class PolicyConstants
{
    public const string DbEntityIdFieldName = "_id";
    public const int MaxNestedLevelIterationLimit = 3;
}
