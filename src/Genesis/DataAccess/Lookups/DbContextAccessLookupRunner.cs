using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis.DataAccess.Lookups;

/// <summary>
/// Runs access lookups for <see cref="DataAccessResolver"/> in the database it reads the caller's schemas from: the
/// caller's tenant database (<see cref="IDbContextProvider.GetDatabase(string)"/>), or the default database when the
/// caller has no tenant. System entities' collections live there whatever data source the tenant's other collections
/// use. The caller is read from the same <see cref="IAccessPrincipalAccessor"/> as the resolver, so a host acting for
/// another tenant looks into that tenant's rows, never its own.
/// </summary>
public sealed class DbContextAccessLookupRunner(IDbContextProvider dbContextProvider, IAccessPrincipalAccessor principals) : IAccessLookupRunner
{
    private readonly IDbContextProvider _dbContextProvider = dbContextProvider ?? throw new ArgumentNullException(nameof(dbContextProvider));
    private readonly IAccessPrincipalAccessor _principals = principals ?? throw new ArgumentNullException(nameof(principals));

    /// <inheritdoc />
    /// <remarks><c>aggregate([{$match}, {$group: {_id: "$field"}}, {$limit}])</c>; the grouped <c>_id</c> values, as stored.</remarks>
    public async Task<IReadOnlyList<BsonValue>> RunAsync(AccessLookupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tenantId = _principals.Current.TenantId;
        var database = string.IsNullOrWhiteSpace(tenantId)
            ? _dbContextProvider.GetDatabase() ?? throw new InvalidOperationException("Database context provider returned null database.")
            : _dbContextProvider.GetDatabase(tenantId);

        var pipeline = new[]
        {
            new BsonDocument("$match", query.Match),
            new BsonDocument("$group", new BsonDocument("_id", "$" + query.DbFieldName)),
            new BsonDocument("$limit", query.Limit),
        };
        using var cursor = await database.GetCollection<BsonDocument>(query.CollectionName)
            .AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken).ConfigureAwait(false);
        var groups = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        return groups.Select(g => g["_id"]).ToList();
    }
}
