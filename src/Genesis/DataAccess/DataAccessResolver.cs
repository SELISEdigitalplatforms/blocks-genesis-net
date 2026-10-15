using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
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

    public DataAccessResolver(IDbContextProvider dbContextProvider, IDataAccessPolicyResolutionService resolver, IAccessPrincipalAccessor principals, DataAccessOptions? options = null)
    {
        _systemEntitiesOnly = options?.SystemEntitiesOnly ?? false;
        _dbContextProvider = dbContextProvider;
        _resolver = resolver;
        _principals = principals;
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

        var restriction = _resolver.RestrictInput(request, input);
        return new ResolvedInput(rls, input, restriction.RemovedFieldNames);
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

        var filter = _resolver.ApplyMutationFilter(request, baseFilter, rls);
        var restriction = _resolver.RestrictInput(request, input);
        return new ResolvedUpdate(rls, filter, input, restriction.RemovedFieldNames);
    }

    /// <inheritdoc />
    public Task<ResolvedFilter> ResolveDeleteAsync(string schemaName, BsonDocument baseFilter, CancellationToken cancellationToken = default) =>
        ResolveMutationFilterAsync(schemaName, PolicyOperation.DELETE, baseFilter, cancellationToken);

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
