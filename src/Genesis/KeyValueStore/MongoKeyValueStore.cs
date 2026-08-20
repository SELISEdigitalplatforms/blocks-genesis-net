using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Blocks.Genesis;

public sealed class MongoKeyValueStore : IKeyValueStore
{
    internal const string CollectionName = "keyValueStores";
    internal const string KeyIndexName = "keyValueStores_Key_Unique";

    private static readonly ConcurrentDictionary<string, byte> _indexedDatabases = new();
    private readonly IDbContextProvider _dbContextProvider;
    private readonly IBlocksSecret _blocksSecret;

    public MongoKeyValueStore(IDbContextProvider dbContextProvider, IBlocksSecret blocksSecret)
    {
        _dbContextProvider = dbContextProvider;
        _blocksSecret = blocksSecret;
    }

    public async Task<T?> GetAsync<T>(string key, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        var entry = await Collection(impersonated)
            .Find(x => x.Key == normalizedKey)
            .FirstOrDefaultAsync(cancellationToken);

        return entry is null || entry.Value.IsBsonNull
            ? default
            : MongoDB.Bson.Serialization.BsonSerializer.Deserialize<T>(entry.Value.ToJson());
    }

    public async Task<IReadOnlyList<T>> GetByPrefixAsync<T>(string prefix, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedPrefix = NormalizeKey(prefix);
        var filter = Builders<KeyValueEntry>.Filter.Regex(
            x => x.Key,
            new BsonRegularExpression($"^{Regex.Escape(normalizedPrefix)}"));

        var entries = await Collection(impersonated)
            .Find(filter)
            .ToListAsync(cancellationToken);

        return entries
            .Where(entry => !entry.Value.IsBsonNull)
            .Select(entry => MongoDB.Bson.Serialization.BsonSerializer.Deserialize<T>(entry.Value.ToJson()))
            .ToList();
    }

    public async Task SetAsync<T>(string key, T value, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        ArgumentNullException.ThrowIfNull(value);
        await EnsureIndexesAsync(impersonated, cancellationToken);

        var now = DateTime.UtcNow;
        var userId = ResolveCurrentUserId();
        var organizationId = ResolveCurrentOrganizationId();
        var bsonValue = SerializeValue(value);

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
            await Collection(impersonated).UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            await Collection(impersonated).UpdateOneAsync(
                filter,
                Builders<KeyValueEntry>.Update
                    .Set(x => x.Value, bsonValue)
                    .Set(x => x.LastUpdatedDate, now)
                    .Set(x => x.LastUpdatedBy, userId),
                cancellationToken: cancellationToken);
        }
    }

    public async Task<bool> DeleteAsync(string key, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        var result = await Collection(impersonated).DeleteOneAsync(x => x.Key == normalizedKey, cancellationToken);
        return result.DeletedCount > 0;
    }

    public async Task<bool> ExistsAsync(string key, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        return await Collection(impersonated)
            .Find(x => x.Key == normalizedKey)
            .Limit(1)
            .AnyAsync(cancellationToken);
    }

    private async Task EnsureIndexesAsync(bool impersonated, CancellationToken cancellationToken)
    {
        var database = GetDatabase(impersonated);

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

    private IMongoCollection<KeyValueEntry> Collection(bool impersonated) =>
        GetDatabase(impersonated).GetCollection<KeyValueEntry>(CollectionName);

    private IMongoDatabase GetDatabase(bool impersonated)
    {
        if (impersonated)
        {
            return _dbContextProvider.GetDatabase()
                ?? throw new InvalidOperationException("Database context is not available. Ensure the tenant ID is set correctly.");
        }

        return _dbContextProvider.GetDatabase(_blocksSecret.DatabaseConnectionString, _blocksSecret.RootDatabaseName);
    }

    private static BsonValue SerializeValue<T>(T value)
    {
        var wrapper = new BsonDocument();
        using (var writer = new BsonDocumentWriter(wrapper))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            MongoDB.Bson.Serialization.BsonSerializer.Serialize(writer, value);
            writer.WriteEndDocument();
        }

        return wrapper["v"];
    }

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
