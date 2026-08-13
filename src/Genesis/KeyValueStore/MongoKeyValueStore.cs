using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis;

public sealed class MongoKeyValueStore : IKeyValueStore
{
    internal const string CollectionName = "keyValueStores";
    internal const string KeyIndexName = "keyValueStores_Key_Unique";

    private static readonly ConcurrentDictionary<string, byte> _indexedDatabases = new();
    private readonly IDbContextProvider _dbContextProvider;

    public MongoKeyValueStore(IDbContextProvider dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        var entry = await Collection()
            .Find(x => x.Key == normalizedKey)
            .FirstOrDefaultAsync(cancellationToken);

        return entry is null || entry.Value.IsBsonNull
            ? default
            : MongoDB.Bson.Serialization.BsonSerializer.Deserialize<T>(entry.Value.ToJson());
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        ArgumentNullException.ThrowIfNull(value);
        await EnsureIndexesAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var userId = ResolveCurrentUserId();
        var organizationId = ResolveCurrentOrganizationId();
        var bsonValue = value.ToBsonDocument(typeof(T));

        var filter = Builders<KeyValueEntry>.Filter.Eq(x => x.Key, normalizedKey);
        var update = Builders<KeyValueEntry>.Update
            .SetOnInsert(x => x.ItemId, ObjectId.GenerateNewId().ToString())
            .SetOnInsert(x => x.Key, normalizedKey)
            .SetOnInsert(x => x.CreatedDate, now)
            .SetOnInsert(x => x.CreatedBy, userId)
            .SetOnInsert(x => x.OrganizationId, organizationId)
            .SetOnInsert(x => x.Tags, [])
            .Set(x => x.Value, bsonValue)
            .Set(x => x.LastUpdatedDate, now)
            .Set(x => x.LastUpdatedBy, userId);

        try
        {
            await Collection().UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            await Collection().UpdateOneAsync(
                filter,
                Builders<KeyValueEntry>.Update
                    .Set(x => x.Value, bsonValue)
                    .Set(x => x.LastUpdatedDate, now)
                    .Set(x => x.LastUpdatedBy, userId),
                cancellationToken: cancellationToken);
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        var result = await Collection().DeleteOneAsync(x => x.Key == normalizedKey, cancellationToken);
        return result.DeletedCount > 0;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        return await Collection()
            .Find(x => x.Key == normalizedKey)
            .Limit(1)
            .AnyAsync(cancellationToken);
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var database = _dbContextProvider.GetDatabase()
            ?? throw new InvalidOperationException("Database context is not available. Ensure the tenant ID is set correctly.");

        var databaseKey = $"{database.Client.GetHashCode()}:{database.DatabaseNamespace.DatabaseName}";
        if (_indexedDatabases.ContainsKey(databaseKey))
        {
            return;
        }

        var collection = database.GetCollection<KeyValueEntry>(CollectionName);
        var index = new CreateIndexModel<KeyValueEntry>(
            Builders<KeyValueEntry>.IndexKeys.Ascending(x => x.Key),
            new CreateIndexOptions<KeyValueEntry>
            {
                Name = KeyIndexName,
                Unique = true
            });

        await collection.Indexes.CreateOneAsync(index, cancellationToken: cancellationToken);
        _indexedDatabases.TryAdd(databaseKey, 0);
    }

    private IMongoCollection<KeyValueEntry> Collection() =>
        _dbContextProvider.GetCollection<KeyValueEntry>(CollectionName);

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }

        return key.Trim();
    }

    private static string? ResolveCurrentUserId()
    {
        var userId = BlocksContext.GetContext()?.UserId;
        return string.IsNullOrWhiteSpace(userId) ? null : userId;
    }

    private static string ResolveCurrentOrganizationId()
    {
        var organizationId = BlocksContext.GetContext()?.OrganizationId;
        return string.IsNullOrWhiteSpace(organizationId) ? "default" : organizationId;
    }
}
