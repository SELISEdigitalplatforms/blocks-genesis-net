using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Composes the relocated helpers behind <see cref="IDataAccessPolicyResolutionService"/>. Stateless, so it
/// is registered as a singleton. The method bodies are the gateway's former private methods, with
/// the ambient request reads replaced by the request's principal.
/// </summary>
public sealed class DataAccessPolicyResolutionService : IDataAccessPolicyResolutionService
{
    private readonly IAccessPolicySource _policySource;

    public DataAccessPolicyResolutionService(IAccessPolicySource policySource)
    {
        _policySource = policySource;
    }

    /// <inheritdoc />
    public PolicyEvaluationResult Check(SchemaAccessRequest request) =>
        request.Operation == PolicyOperation.READ
            ? CheckRead(request)
            : CheckMutation(request);

    // Former QueryService.EvaluateRlsPolicies: gates on the read level whatever the operation and hands
    // every RLS policy to the evaluator, which includes ALL.
    private PolicyEvaluationResult CheckRead(SchemaAccessRequest request)
    {
        var (principal, schema, operation) = request;
        if (schema.ReadAccessLevel != SchemaAccessLevel.Custom || principal.IsRootTenant)
            return new PolicyEvaluationResult { IsAccessGranted = true };

        var rlsPolicies = _policySource.GetPolicies(schema).Where(p => p.PolicyType == PolicyType.RLS).ToList();
        if (rlsPolicies.Count == 0)
            return PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess);

        return rlsPolicies.EvaluatePolicies(principal, operation, PolicyType.RLS);
    }

    // Former MutationService.EvaluateRlsPolicies: gates on the operation's level and pre-filters on the
    // exact operation, so ALL-only policies do not apply to mutations.
    private PolicyEvaluationResult CheckMutation(SchemaAccessRequest request)
    {
        var (principal, schema, operation) = request;
        var accessLevel = ClsInputHelper.GetSchemaAccessLevelForOperation(schema, operation);
        if (accessLevel != SchemaAccessLevel.Custom || principal.IsRootTenant)
            return new PolicyEvaluationResult { IsAccessGranted = true };

        var rlsPolicies = _policySource.GetPolicies(schema)
            .Where(p => p.PolicyType == PolicyType.RLS && p.Operation == operation)
            .ToList();
        if (rlsPolicies.Count == 0)
            return PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess);

        return rlsPolicies.EvaluatePolicies(principal, operation, PolicyType.RLS);
    }

    /// <inheritdoc />
    public BsonDocument BuildReadFilter(SchemaAccessRequest request, BsonDocument userFilter, PolicyEvaluationResult rls)
    {
        // Former QueryService.BuildMongoFilter.
        var useCustomRls = request.Schema.ReadAccessLevel == SchemaAccessLevel.Custom && !request.Principal.IsRootTenant;
        if (useCustomRls)
            return RlsFilterHelper.CombineUserFilterWithRls(userFilter, rls);

        if (userFilter.ElementCount == 0)
            return new BsonDocument();

        var filterDoc = new BsonDocument(userFilter);
        return SystemFieldHelper.ReplaceSystemFieldInFilter(filterDoc);
    }

    /// <inheritdoc />
    public BsonDocument ApplyMutationFilter(SchemaAccessRequest request, BsonDocument baseFilter, PolicyEvaluationResult rls)
    {
        // Former tail of MutationFilterHelper.BuildFilterWithRls.
        var useCustomAccess = ClsInputHelper.GetSchemaAccessLevelForOperation(request.Schema, request.Operation) == SchemaAccessLevel.Custom;
        return useCustomAccess ? RlsFilterHelper.ApplyPolicyFilter(baseFilter, rls) : baseFilter;
    }

    /// <inheritdoc />
    public ReadProjection BuildReadProjection(SchemaAccessRequest request, BsonDocument requestedProjection)
    {
        var projection = ClsProjectionHelper.ApplyReadClsPolicies(
            request.Principal, requestedProjection, request.Schema, out var evaluationOnlyFieldPaths);
        return new ReadProjection(projection, evaluationOnlyFieldPaths, GetReadClsPolicies(request));
    }

    /// <inheritdoc />
    public ReadProjection BuildReadProjection(SchemaAccessRequest request, IReadOnlyList<string>? requestedFields)
    {
        var projection = ClsProjectionHelper.BuildMongoProjectionWithCls(
            request.Principal, requestedFields, request.Schema, out var evaluationOnlyFieldPaths);
        return new ReadProjection(projection, evaluationOnlyFieldPaths, GetReadClsPolicies(request));
    }

    // Former QueryService.GetReadClsPolicies.
    private List<DataAccessPolicy> GetReadClsPolicies(SchemaAccessRequest request)
    {
        if (request.Principal.IsRootTenant) return [];
        return _policySource.GetPolicies(request.Schema)
            .Where(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.READ)
            .ToList();
    }

    /// <inheritdoc />
    public Dictionary<string, object> MaskRow(SchemaAccessRequest request, Dictionary<string, object> row, ReadProjection projection)
    {
        // Former QueryService.BuildResultItems, per row.
        var principal = request.Principal;
        if (principal.IsRootTenant)
            return row;

        return QueryClsRowMaskingHelper.MaskRowByClsPolicies(
            principal,
            row,
            request.Schema,
            projection.ClsReadPolicies,
            projection.EvaluationOnlyFieldPaths,
            QueryPolicyRuleEvaluator.OncePerRuleGroup(principal));
    }

    /// <inheritdoc />
    public InputRestriction RestrictInput(SchemaAccessRequest request, IDictionary<string, object?> input)
    {
        // Former MutationService.ApplyClsRestrictionsToInput, minus the logging the service keeps.
        var allPaths = ClsInputHelper.GetAllPathsFromInput(input, prefix: "");
        var clsResult = ClsInputHelper.EvaluateClsPoliciesForInput(request.Principal, request.Schema, request.Operation, allPaths);
        var removedFieldNames = ClsInputHelper.RemoveExcludedPathsFromInput(input, clsResult.ExcludedFields);
        return new InputRestriction(clsResult.ExcludedFields, removedFieldNames);
    }

    /// <inheritdoc />
    public PolicyEvaluationResult CheckRow(SchemaAccessRequest request, PolicyEvaluationResult rls, BsonDocument row)
    {
        ArgumentNullException.ThrowIfNull(rls);
        ArgumentNullException.ThrowIfNull(row);
        if (!rls.IsAccessGranted)
            return rls;
        if (ClsInputHelper.GetSchemaAccessLevelForOperation(request.Schema, request.Operation) != SchemaAccessLevel.Custom ||
            request.Principal.IsRootTenant ||
            !rls.RequiresDataFilter ||
            rls.DataFilter.ElementCount == 0)
            return rls;

        try
        {
            return BsonFilterMatcher.Matches(row, rls.DataFilter)
                ? rls
                : PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess);
        }
        catch (NotSupportedException)
        {
            // Fail closed: a row rule we cannot check must not let the write through.
            return PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess);
        }
    }

    /// <inheritdoc />
    public PolicyEvaluationResult CheckInput(SchemaAccessRequest request, IEnumerable<string> paths, BsonDocument? dataRow)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<string> denied;
        try
        {
            denied = ClsInputHelper.FindDeniedPaths(request.Principal, request.Schema, request.Operation, paths, dataRow);
        }
        catch (NotSupportedException)
        {
            return PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess);
        }

        if (denied.Count == 0)
            return new PolicyEvaluationResult { IsAccessGranted = true };

        var verb = request.Operation == PolicyOperation.WRITE ? "write" : "edit";
        var names = string.Join(", ", denied.Select(d => $"'{d}'"));
        return PolicyEvaluationResult.Forbidden($"No policy grants {verb} access to field {names}.", denied);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<PolicyOperation> ListAllowed(AccessPrincipal principal, SchemaDefinitionExtended schema)
    {
        PolicyOperation[] operations = [PolicyOperation.READ, PolicyOperation.WRITE, PolicyOperation.EDIT, PolicyOperation.DELETE];
        return operations
            .Where(operation => Check(new SchemaAccessRequest(principal, schema, operation)).IsAccessGranted)
            .ToList();
    }
}
