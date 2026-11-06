using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// The single entry point for DataAccessPolicy decisions. Each method is a direct call into the
/// evaluation code that used to live inside the gateway's query and mutation services; no rule is
/// added or changed here, the resolver only gives that code one home and one caller contract.
/// </summary>
public interface IDataAccessPolicyResolutionService
{
    /// <summary>
    /// The RLS gate for the operation. READ keeps the query service's gate (schema read level, every
    /// RLS policy, <c>ALL</c> included); WRITE/EDIT/DELETE keep the mutation service's gate (the
    /// operation's level, only policies declared for exactly that operation).
    /// </summary>
    PolicyEvaluationResult Check(SchemaAccessRequest request);

    /// <summary>The query service's filter: the user filter combined with the RLS data filter when the schema uses Custom read access.</summary>
    BsonDocument BuildReadFilter(SchemaAccessRequest request, BsonDocument userFilter, PolicyEvaluationResult rls);

    /// <summary>The mutation filter: the base filter combined with the RLS data filter when the operation's level is Custom.</summary>
    BsonDocument ApplyMutationFilter(SchemaAccessRequest request, BsonDocument baseFilter, PolicyEvaluationResult rls);

    /// <summary>Adds CLS-protected and rule-operand fields to the requested projection and collects the READ CLS policies.</summary>
    ReadProjection BuildReadProjection(SchemaAccessRequest request, BsonDocument requestedProjection);

    /// <summary>Same as <see cref="BuildReadProjection(SchemaAccessRequest, BsonDocument)"/> from a plain list of requested field paths.</summary>
    ReadProjection BuildReadProjection(SchemaAccessRequest request, IReadOnlyList<string>? requestedFields);

    /// <summary>Applies CLS masking to one result row. The row is modified in place and returned.</summary>
    Dictionary<string, object> MaskRow(SchemaAccessRequest request, Dictionary<string, object> row, ReadProjection projection);

    /// <summary>Removes the input paths CLS does not allow for this caller. The input is modified in place.</summary>
    InputRestriction RestrictInput(SchemaAccessRequest request, IDictionary<string, object?> input);

    /// <summary>
    /// The row rule on a written row (insert: the new row against the WRITE row filter; update: the stored row and
    /// the resulting row against the EDIT row filter). <paramref name="rls"/> is the result of <see cref="Check"/> for
    /// the same request. Granted when the operation's level is not Custom, for the root tenant, and when the policy
    /// needs no data filter. A row outside the filter, or a filter that cannot be checked in memory, is a denial with
    /// code <see cref="PolicyErrorCodes.Forbidden"/>.
    /// </summary>
    PolicyEvaluationResult CheckRow(SchemaAccessRequest request, PolicyEvaluationResult rls, BsonDocument row);

    /// <summary>
    /// The column rules on written paths, refusing instead of stripping: denied when any path may not be written,
    /// with those paths in <see cref="PolicyEvaluationResult.ExcludedFields"/> and named in the message. Column rules
    /// that read a schema field are checked against <paramref name="dataRow"/> (the stored row on EDIT, the new row on
    /// WRITE); without a row they pass on their token part alone. Paths the schema does not define are not checked here.
    /// </summary>
    PolicyEvaluationResult CheckInput(SchemaAccessRequest request, IEnumerable<string> paths, BsonDocument? dataRow);

    /// <summary>The operations whose RLS gate passes for this caller on this schema.</summary>
    IReadOnlyCollection<PolicyOperation> ListAllowed(AccessPrincipal principal, SchemaDefinitionExtended schema);
}
