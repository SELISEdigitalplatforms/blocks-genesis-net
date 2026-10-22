using System.Collections.Concurrent;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis.DataAccess;

/// <summary>
/// Reads <c>SchemaDefinitions</c>, <c>DataAccessPolicys</c> and <c>DataValidations</c> of the
/// principal's tenant (collections named after the entity type, as the gateway names them) and maps
/// them onto a <see cref="SchemaDefinitionExtended"/>. Only the schema's own rows are loaded; the
/// policies are attached as stored, which is all the row-level gate and filter need.
/// </summary>
public sealed class DataAccessResolver : IDataAccessResolver
{
    private readonly IDbContextProvider _dbContextProvider;
    private readonly IDataAccessPolicyResolutionService _resolver;
    private readonly IAccessPrincipalAccessor _principals;
    private readonly bool _systemEntitiesOnly;

    public DataAccessResolver(IDbContextProvider dbContextProvider, IDataAccessPolicyResolutionService resolver, IAccessPrincipalAccessor principals, DataAccessOptions? options = null,
        ILogger<DataAccessResolver>? logger = null)
    {
        _systemEntitiesOnly = options?.SystemEntitiesOnly ?? false;
        _dbContextProvider = dbContextProvider;
        _resolver = resolver;
        _principals = principals;
        _logger = logger ?? NullLogger<DataAccessResolver>.Instance;
    }

    private readonly ILogger<DataAccessResolver> _logger;

    /// <summary>Rules and defaults the allow-list ignored, each logged once per process.</summary>
    private static readonly ConcurrentDictionary<string, byte> IgnoredLogged = new(StringComparer.Ordinal);

    private void LogIgnored(string what)
    {
        if (IgnoredLogged.TryAdd(what, 0))
            _logger.LogWarning("Ignored {RuleOrDefault}: the system schema allow-list does not open this property.", what);
    }

    /// <inheritdoc />
    public async Task<ResolvedFilter> ResolveReadFilterAsync(string schemaName, BsonDocument userFilter, CancellationToken cancellationToken = default)
    {
        var request = await PrepareRequestAsync(schemaName, PolicyOperation.READ, cancellationToken);
        var rls = _resolver.Check(request);
        if (!rls.IsAccessGranted)
            return new ResolvedFilter(rls, null);

        return new ResolvedFilter(rls, _resolver.BuildReadFilter(request, userFilter, rls));
    }

    /// <inheritdoc />
    public async Task<ResolvedFilter> ResolveMutationFilterAsync(string schemaName, PolicyOperation operation, BsonDocument baseFilter, CancellationToken cancellationToken = default)
    {
        var request = await PrepareRequestAsync(schemaName, operation, cancellationToken);
        var rls = _resolver.Check(request);
        if (!rls.IsAccessGranted)
            return new ResolvedFilter(rls, null);

        return new ResolvedFilter(rls, _resolver.ApplyMutationFilter(request, baseFilter, rls));
    }

    /// <inheritdoc />
    public async Task<ResolvedRead> ResolveReadAsync(string schemaName, BsonDocument userFilter, IReadOnlyList<string>? requestedFields, CancellationToken cancellationToken = default)
    {
        var request = await PrepareRequestAsync(schemaName, PolicyOperation.READ, cancellationToken);
        var rls = _resolver.Check(request);
        if (!rls.IsAccessGranted)
            return new ResolvedRead(rls, null, null, null);

        var filter = _resolver.BuildReadFilter(request, userFilter, rls);
        var projection = _resolver.BuildReadProjection(request, requestedFields);
        return new ResolvedRead(rls, filter, projection.Projection, row => _resolver.MaskRow(request, row, projection));
    }

    /// <inheritdoc />
    public async Task<ResolvedProjection> ResolveProjectionAsync(string schemaName, IReadOnlyList<string>? requestedFields, CancellationToken cancellationToken = default)
    {
        var request = await PrepareRequestAsync(schemaName, PolicyOperation.READ, cancellationToken);
        var rls = _resolver.Check(request);
        if (!rls.IsAccessGranted)
            return new ResolvedProjection(rls, null, null);

        var projection = _resolver.BuildReadProjection(request, requestedFields);
        return new ResolvedProjection(rls, projection.Projection, row => _resolver.MaskRow(request, row, projection));
    }

    /// <inheritdoc />
    public async Task<ResolvedInput> ResolveInputAsync(string schemaName, PolicyOperation operation, IDictionary<string, object?> input, CancellationToken cancellationToken = default)
    {
        var request = await PrepareRequestAsync(schemaName, operation, cancellationToken);
        var rls = _resolver.Check(request);
        if (!rls.IsAccessGranted)
            return new ResolvedInput(rls, null, []);

        var removed = DropUnknownRootFields(request, input);
        var row = operation == PolicyOperation.WRITE ? InputDocument.From(input) : null;

        // An empty insert input is the WRITE gate alone (the services ask it before building the entity).
        if (row is { ElementCount: > 0 })
        {
            var rowCheck = _resolver.CheckRow(request, rls, row);
            if (!rowCheck.IsAccessGranted)
                return new ResolvedInput(rowCheck, null, []);
        }

        var columns = _resolver.CheckInput(request, ClsInputHelper.GetAllPathsFromInput(input, prefix: ""), row);
        return columns.IsAccessGranted ? new ResolvedInput(rls, input, removed) : new ResolvedInput(columns, null, []);
    }

    /// <summary>Root fields the schema does not define are dropped, as before; fields a rule refuses are not dropped.</summary>
    private static List<string> DropUnknownRootFields(SchemaAccessRequest request, IDictionary<string, object?> input)
    {
        if (request.Principal.IsRootTenant)
            return [];
        var unknown = ClsInputHelper.UnknownRootPaths(request.Schema, input.Keys);
        return ClsInputHelper.RemoveExcludedPathsFromInput(input, unknown);
    }

    /// <inheritdoc />
    public Task<ResolvedInput> ResolveInsertAsync(string schemaName, IDictionary<string, object?> input, CancellationToken cancellationToken = default) =>
        ResolveInputAsync(schemaName, PolicyOperation.WRITE, input, cancellationToken);

    /// <inheritdoc />
    public async Task<ResolvedUpdate> ResolveUpdateAsync(string schemaName, BsonDocument baseFilter, IDictionary<string, object?> input, CancellationToken cancellationToken = default)
    {
        var request = await PrepareRequestAsync(schemaName, PolicyOperation.EDIT, cancellationToken);
        var rls = _resolver.Check(request);
        if (!rls.IsAccessGranted)
            return new ResolvedUpdate(rls, null, null, []);

        var removed = DropUnknownRootFields(request, input);
        var columns = _resolver.CheckInput(request, ClsInputHelper.GetAllPathsFromInput(input, prefix: ""), dataRow: null);
        if (!columns.IsAccessGranted)
            return new ResolvedUpdate(columns, null, null, []);

        var filter = _resolver.ApplyMutationFilter(request, baseFilter, rls);
        return new ResolvedUpdate(rls, filter, input, removed);
    }

    /// <inheritdoc />
    public async Task<ResolvedRowCheck> CheckInsertAsync(string schemaName, BsonDocument row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        var request = await PrepareRequestAsync(schemaName, PolicyOperation.WRITE, cancellationToken).ConfigureAwait(false);
        var rls = _resolver.Check(request);
        if (!rls.IsAccessGranted)
            return new ResolvedRowCheck(rls);

        // The row rule first: a row outside it would otherwise fail every inherited column rule and name every field.
        var rowCheck = _resolver.CheckRow(request, rls, row);
        if (!rowCheck.IsAccessGranted)
            return new ResolvedRowCheck(rowCheck);

        var columns = _resolver.CheckInput(request, RowChanges.AllPaths(row), row);
        return new ResolvedRowCheck(columns.IsAccessGranted ? rls : columns);
    }

    /// <inheritdoc />
    public Task<ResolvedRowCheck> CheckReplaceAsync(string schemaName, BsonDocument storedRow, BsonDocument newRow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storedRow);
        ArgumentNullException.ThrowIfNull(newRow);
        return CheckEditAsync(schemaName, storedRow, new RowUpdateResult(newRow, RowChanges.ChangedPaths(storedRow, newRow), []), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ResolvedRowCheck> CheckUpdateAsync(string schemaName, BsonDocument storedRow, BsonDocument update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storedRow);
        ArgumentNullException.ThrowIfNull(update);
        return CheckEditAsync(schemaName, storedRow, RowChanges.ApplyUpdate(storedRow, update), cancellationToken);
    }

    private async Task<ResolvedRowCheck> CheckEditAsync(string schemaName, BsonDocument storedRow, RowUpdateResult update, CancellationToken cancellationToken)
    {
        var request = await PrepareRequestAsync(schemaName, PolicyOperation.EDIT, cancellationToken).ConfigureAwait(false);
        var rls = _resolver.Check(request);
        if (!rls.IsAccessGranted)
            return new ResolvedRowCheck(rls);

        var stored = _resolver.CheckRow(request, rls, storedRow);
        if (!stored.IsAccessGranted)
            return new ResolvedRowCheck(stored);

        if (TouchesRowRule(request, rls, update.UncheckablePaths))
            return new ResolvedRowCheck(PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess));

        var result = _resolver.CheckRow(request, rls, update.Row);
        if (!result.IsAccessGranted)
            return new ResolvedRowCheck(result);

        var columns = _resolver.CheckInput(request, update.ChangedPaths, storedRow);
        return new ResolvedRowCheck(columns.IsAccessGranted ? rls : columns);
    }

    /// <summary>True when an update changes, in a way not computed in memory, a field the row rule reads.</summary>
    private static bool TouchesRowRule(SchemaAccessRequest request, PolicyEvaluationResult rls, IReadOnlyList<string> uncheckablePaths)
    {
        if (uncheckablePaths.Count == 0 || request.Principal.IsRootTenant || !rls.RequiresDataFilter ||
            ClsInputHelper.GetSchemaAccessLevelForOperation(request.Schema, request.Operation) != SchemaAccessLevel.Custom)
            return false;

        var referenced = BsonFilterMatcher.ReferencedPaths(rls.DataFilter);
        return uncheckablePaths
            .Select(p => string.Join('.', p.Split('.').TakeWhile(s => !s.StartsWith('$') && !int.TryParse(s, out _))))
            .Any(p => p.Length == 0 || referenced.Any(r => r == p ||
                r.StartsWith(p + ".", StringComparison.Ordinal) || p.StartsWith(r + ".", StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public Task<ResolvedFilter> ResolveDeleteAsync(string schemaName, BsonDocument baseFilter, CancellationToken cancellationToken = default) =>
        ResolveMutationFilterAsync(schemaName, PolicyOperation.DELETE, baseFilter, cancellationToken);

    /// <inheritdoc />
    public async Task<ResolvedWrite> ResolveWriteAsync(string schemaName, BsonDocument document, bool isInsert, BsonDocument? storedRecord = null, CancellationToken cancellationToken = default)
    {
        var principal = _principals.Current;
        var schema = await LoadSchemaAsync(schemaName, principal, cancellationToken)
            ?? throw new KeyNotFoundException($"Schema '{schemaName}' was not found for tenant '{principal.TenantId}'.");

        var result = new DataValidationResult();
        if (!WriteRules.KeepAllowedOnly(schema, LogIgnored))
            return new ResolvedWrite(document, result, DefaultsApplied: false);

        var record = WriteRules.ToRecord(document);
        var defaultsApplied = false;
        if (isInsert && FieldDefaultsApplier.HasDefaults(schema.Fields))
        {
            var defaults = FieldDefaultsApplier.Compute(record, schema.SchemaName, schema.Fields, principal, DateTime.UtcNow);
            FieldDefaultsApplier.Apply(record, defaults);
            defaultsApplied = WriteRules.ApplyDefaults(document, defaults) > 0;
        }

        result.Errors.AddRange(record.Validate(schema, storedRecord is null ? null : WriteRules.ToRecord(storedRecord)).Errors);
        await CheckReferencesAsync(schema, record, principal, result, cancellationToken);
        return new ResolvedWrite(document, result, defaultsApplied);
    }

    /// <summary>Reference rules: each value (each item, for a list) must exist in the target schema's collection.</summary>
    private async Task CheckReferencesAsync(SchemaDefinitionExtended schema, Dictionary<string, object?> record, AccessPrincipal principal, DataValidationResult result, CancellationToken cancellationToken)
    {
        var targetNames = WriteRules.ReferencedSchemaNames(schema.Fields);
        if (targetNames.Count == 0)
            return;

        var schemas = new List<SchemaDefinitionExtended> { schema };
        foreach (var name in targetNames.Where(n => n != schema.SchemaName))
        {
            if (await LoadSchemaAsync(name, principal, cancellationToken) is { } target)
                schemas.Add(target);
        }

        var targets = ReferenceTargets.Build(schemas, strict: false, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var values = ReferenceValueCollector.Collect([record], schema, targets);
        if (values.Count == 0)
            return;

        var database = Database(principal);
        foreach (var group in values.GroupBy(v => (v.Target.CollectionName, v.Target.DbFieldName)))
        {
            var requested = group.Select(v => BsonValue.Create(v.Value)).Distinct().ToList();
            var collection = database.GetCollection<BsonDocument>(group.Key.CollectionName);
            var found = await collection.DistinctAsync<BsonValue>(group.Key.DbFieldName, Builders<BsonDocument>.Filter.In(group.Key.DbFieldName, requested), cancellationToken: cancellationToken);
            var present = (await found.ToListAsync(cancellationToken)).Select(WriteRules.ReferenceKey).ToHashSet(StringComparer.Ordinal);

            foreach (var value in group.Where(v => !present.Contains(WriteRules.ReferenceKey(BsonValue.Create(v.Value)))))
                result.AddError(value.Path, WriteRules.ReferenceNotFoundMessage(schema.SchemaName, value), nameof(ValidationType.Reference));
        }
    }

    private IMongoDatabase Database(AccessPrincipal principal) =>
        string.IsNullOrWhiteSpace(principal.TenantId)
            ? _dbContextProvider.GetDatabase() ?? throw new InvalidOperationException("Database context provider returned null database.")
            : _dbContextProvider.GetDatabase(principal.TenantId);

    private async Task<SchemaAccessRequest> PrepareRequestAsync(string schemaName, PolicyOperation operation, CancellationToken cancellationToken)
    {
        var principal = _principals.Current;
        var schema = await LoadSchemaAsync(schemaName, principal, cancellationToken)
            ?? throw new KeyNotFoundException($"Schema '{schemaName}' was not found for tenant '{principal.TenantId}'.");
        return new SchemaAccessRequest(principal, schema, operation);
    }

    /// <summary>
    /// The schema with its policies and validations, assembled as the gateway assembles it at publish time
    /// (field tree, inherited column policies, cleanup). Null when the principal's tenant has no such schema.
    /// </summary>
    public async Task<SchemaDefinitionExtended?> LoadSchemaAsync(string schemaName, AccessPrincipal principal, CancellationToken cancellationToken = default)
    {
        var tenantId = principal.TenantId;
        var database = string.IsNullOrWhiteSpace(tenantId)
            ? _dbContextProvider.GetDatabase() ?? throw new InvalidOperationException("Database context provider returned null database.")
            : _dbContextProvider.GetDatabase(tenantId);

        // Services must never pick up a tenant's own schema that happens to share a system entity's name.
        // Policies and validations below are linked by SchemaId (the chosen schema's ItemId), never by name.
        var definition = _systemEntitiesOnly
            ? await Collection<SchemaDefinition>(database)
                .Find(d => d.SchemaName == schemaName && !d.IsDeleted && d.IsSystemEntity)
                .FirstOrDefaultAsync(cancellationToken)
            : await Collection<SchemaDefinition>(database)
                .Find(d => d.SchemaName == schemaName && !d.IsDeleted)
                .FirstOrDefaultAsync(cancellationToken);
        if (definition is null)
            return null;

        var policies = await Collection<DataAccessPolicy>(database)
            .Find(p => p.SchemaId == definition.ItemId && !p.IsDeleted)
            .ToListAsync(cancellationToken);

        var validations = await Collection<DataValidation>(database)
            .Find(v => v.SchemaId == definition.ItemId && !v.IsDeleted)
            .ToListAsync(cancellationToken);

        var schema = new SchemaDefinitionExtended
        {
            ItemId = definition.ItemId,
            IsDeleted = definition.IsDeleted,
            SchemaName = definition.SchemaName,
            SchemaType = definition.SchemaType,
            ProjectKey = definition.ProjectKey,
            ProjectShortKey = definition.ProjectShortKey,
            ReadAccessLevel = definition.ReadAccessLevel,
            WriteAccessLevel = definition.WriteAccessLevel,
            EditAccessLevel = definition.EditAccessLevel,
            DeleteAccessLevel = definition.DeleteAccessLevel,
            CollectionName = definition.CollectionName,
            IsSystemEntity = definition.IsSystemEntity,
            Fields = FieldDefinitionMapper.GetFieldDefinitionResponses(definition.Fields, policies, validations),
            Policies = policies
        };

        // Same two steps GraphqlSchemaBuilder.Assemble runs per schema.
        var rlsSchemaPolicies = policies.Where(p => p.PolicyType == PolicyType.RLS);
        SchemaPolicyDerivation.SetFieldsPolicies(schema, schema.Fields, rlsSchemaPolicies, schema.ItemId, string.Empty, principal);
        SchemaPolicyDerivation.CleanupClsPoliciesBaseOnSchemaAccessLevel(schema);
        return schema;
    }

    private static IMongoCollection<T> Collection<T>(IMongoDatabase database) =>
        database.GetCollection<T>($"{typeof(T).Name}s");
}
