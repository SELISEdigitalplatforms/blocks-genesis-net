using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>One caller, one schema, one operation: the unit every resolver method decides on.</summary>
public sealed record SchemaAccessRequest(
    AccessPrincipal Principal,
    SchemaDefinitionExtended Schema,
    PolicyOperation Operation);

/// <summary>
/// The Mongo projection for a read plus what the row masking step needs afterwards: the paths that were
/// added only to evaluate rules (removed from the result) and the READ CLS policies to apply per row.
/// </summary>
public sealed record ReadProjection(
    BsonDocument Projection,
    HashSet<string> EvaluationOnlyFieldPaths,
    List<DataAccessPolicy> ClsReadPolicies);

/// <summary>Result of applying CLS to a mutation input: the paths that were excluded and those actually removed.</summary>
public sealed record InputRestriction(
    List<string> ExcludedFields,
    List<string> RemovedFieldNames);
