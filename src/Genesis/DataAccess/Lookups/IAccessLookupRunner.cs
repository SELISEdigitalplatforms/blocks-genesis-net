using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Lookups;

/// <summary>
/// One access lookup: the distinct values of <see cref="DbFieldName"/> in <see cref="CollectionName"/> among the rows
/// matching <see cref="Match"/>, at most <see cref="Limit"/> of them. A system entity's collection is in the tenant's
/// default database, whatever data source the tenant's other collections use.
/// </summary>
public sealed record AccessLookupQuery(string CollectionName, string DbFieldName, bool IsSystemEntity, BsonDocument Match, int Limit);

/// <summary>Runs access lookups against the database. The caller's own policies are never applied to them.</summary>
public interface IAccessLookupRunner
{
    /// <summary>The distinct values found, as stored (at most <see cref="AccessLookupQuery.Limit"/>).</summary>
    Task<IReadOnlyList<BsonValue>> RunAsync(AccessLookupQuery query, CancellationToken cancellationToken);
}
