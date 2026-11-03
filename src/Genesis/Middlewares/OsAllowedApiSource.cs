using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis;

/// <summary>Reads the root onboarding allow-list document. Exceptions reach the caller.</summary>
internal interface IOsAllowedApiSource
{
    /// <summary>The root <c>IdentityConfigurations</c> document, or <c>null</c> when there is none.</summary>
    Task<BsonDocument?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>AllowedApis</c> from the first <c>IdentityConfigurations</c> document of the root database,
/// projected to <c>_id</c> and <c>AllowedApis</c> and limited to one document.
/// </summary>
internal sealed class MongoOsAllowedApiSource : IOsAllowedApiSource
{
    private const string IdentityConfigurationsCollection = "IdentityConfigurations";
    private const string AllowedApisField = "AllowedApis";

    private static readonly ProjectionDefinition<BsonDocument> _allowedApisOnly =
        Builders<BsonDocument>.Projection.Include(AllowedApisField);

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoOsAllowedApiSource(IMongoDatabase rootDatabase)
    {
        ArgumentNullException.ThrowIfNull(rootDatabase);
        _collection = rootDatabase.GetCollection<BsonDocument>(IdentityConfigurationsCollection);
    }

    public Task<BsonDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        return _collection
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Project<BsonDocument>(_allowedApisOnly)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)!;
    }
}
