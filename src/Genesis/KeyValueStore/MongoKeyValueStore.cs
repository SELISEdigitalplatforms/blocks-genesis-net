using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Blocks.Genesis;

public sealed class MongoKeyValueStore : IKeyValueStore
{
    internal const string CollectionName = "KeyValueStores";

    /// <summary>Non-unique index on <c>Key</c>. Serves key lookups and anchored prefix scans.</summary>
    internal const string KeyIndexName = "KeyValueStores_Key";

    /// <summary>
    /// The camelCase name this store used up to 4.0.10, kept only so the index names it
    /// left behind can be recognised. Mongo collection names are case sensitive, so the
    /// documents do not move on their own - a database is migrated out of band, by
    /// renaming the collection.
    /// </summary>
    internal const string LegacyCollectionName = "keyValueStores";

    /// <summary>
    /// Index names carried over from <see cref="LegacyCollectionName"/>. Both cover
    /// <c>{ Key: 1 }</c>, and MongoDB refuses a second index over an identical key
    /// pattern under a new name, so each has to go before <see cref="KeyIndexName"/>
    /// can be created:
    /// <list type="bullet">
    /// <item><description>
    /// <c>keyValueStores_Key_Unique</c> - the unique index this store shipped with up to
    /// 4.0.9, before a key was allowed to hold several documents.
    /// </description></item>
    /// <item><description>
    /// <c>keyValueStores_Key</c> - its non-unique replacement in 4.0.10. Renaming a
    /// collection preserves its indexes under their existing names, so a migrated
    /// database arrives still carrying this one.
    /// </description></item>
    /// </list>
    /// </summary>
    internal static readonly string[] LegacyKeyIndexNames =
    [
        $"{LegacyCollectionName}_Key_Unique",
        $"{LegacyCollectionName}_Key"
    ];

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
            : Deserialize<T>(entry.Value);
    }

    public async Task<IReadOnlyList<T>> GetByPrefixAsync<T>(string prefix, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var entries = await Collection(impersonated)
            .Find(PrefixFilter(prefix, tags: null))
            .ToListAsync(cancellationToken);

        return entries
            .Where(entry => !entry.Value.IsBsonNull)
            .Select(entry => Deserialize<T>(entry.Value))
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
        // Kept for the window in which a database still carries the legacy unique index
        // (an instance on an older build can recreate it), and for the upsert's own
        // insert/insert race.
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

    public async Task<string> AddAsync<T>(string key, T value, IEnumerable<string>? tags = null, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        ArgumentNullException.ThrowIfNull(value);
        await EnsureIndexesAsync(impersonated, cancellationToken);

        var now = DateTime.UtcNow;
        var userId = ResolveCurrentUserId();

        var entry = new KeyValueEntry
        {
            ItemId = ObjectId.GenerateNewId().ToString(),
            Key = normalizedKey,
            Value = SerializeValue(value),
            Tags = NormalizeTags(tags),
            CreatedDate = now,
            LastUpdatedDate = now,
            CreatedBy = userId,
            LastUpdatedBy = userId,
            OrganizationId = ResolveCurrentOrganizationId()
        };

        await Collection(impersonated).InsertOneAsync(entry, options: null, cancellationToken);
        return entry.ItemId;
    }

    public async Task<IReadOnlyList<KeyValueItem<T>>> GetAllAsync<T>(string key, IEnumerable<string>? tags = null, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var filter = WithTags(Builders<KeyValueEntry>.Filter.Eq(x => x.Key, normalizedKey), tags);

        var entries = await Collection(impersonated).Find(filter).ToListAsync(cancellationToken);
        return ToItems<T>(entries);
    }

    public async Task<IReadOnlyList<KeyValueItem<T>>> GetAllByPrefixAsync<T>(string prefix, IEnumerable<string>? tags = null, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var entries = await Collection(impersonated)
            .Find(PrefixFilter(prefix, tags))
            .ToListAsync(cancellationToken);

        return ToItems<T>(entries);
    }

    public async Task<KeyValueItem<T>?> GetByIdAsync<T>(string itemId, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeItemId(itemId);

        var entry = await Collection(impersonated)
            .Find(x => x.ItemId == normalizedId)
            .FirstOrDefaultAsync(cancellationToken);

        return entry is null || entry.Value.IsBsonNull ? null : ToItem<T>(entry);
    }

    public async Task<bool> UpdateByIdAsync<T>(string itemId, T value, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeItemId(itemId);
        ArgumentNullException.ThrowIfNull(value);

        var update = Builders<KeyValueEntry>.Update
            .Set(x => x.Value, SerializeValue(value))
            .Set(x => x.LastUpdatedDate, DateTime.UtcNow)
            .Set(x => x.LastUpdatedBy, ResolveCurrentUserId());

        var result = await Collection(impersonated).UpdateOneAsync(
            x => x.ItemId == normalizedId,
            update,
            options: null,
            cancellationToken);

        // MatchedCount, not ModifiedCount: rewriting a document with the value it
        // already holds is a successful update that modifies nothing.
        return result.MatchedCount > 0;
    }

    public async Task<bool> DeleteByIdAsync(string itemId, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeItemId(itemId);

        var result = await Collection(impersonated).DeleteOneAsync(x => x.ItemId == normalizedId, cancellationToken);
        return result.DeletedCount > 0;
    }

    public async Task<long> DeleteAllAsync(string key, bool impersonated = true, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        var result = await Collection(impersonated).DeleteManyAsync(x => x.Key == normalizedKey, cancellationToken);
        return result.DeletedCount;
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

        // Drop before create: every legacy index covers the same field, and MongoDB rejects
        // a second index over an identical key pattern under a new name
        // (IndexKeySpecsConflict). A database that never carried one - or was migrated by
        // an earlier run - reports IndexNotFound, which is the steady state.
        foreach (var legacyIndexName in LegacyKeyIndexNames)
        {
            try
            {
                await collection.Indexes.DropOneAsync(legacyIndexName, cancellationToken);
            }
            catch (MongoCommandException exception)
                when (exception.CodeName is "IndexNotFound" or "NamespaceNotFound")
            {
                // Nothing to migrate: the index was never created, or - now that the
                // collection is PascalCase - the whole collection is yet to exist. Both
                // are the steady state, not a fault.
            }
        }

        var index = new CreateIndexModel<KeyValueEntry>(
            Builders<KeyValueEntry>.IndexKeys.Ascending(x => x.Key),
            new CreateIndexOptions<KeyValueEntry>
            {
                Name = KeyIndexName
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

    private static FilterDefinition<KeyValueEntry> PrefixFilter(string prefix, IEnumerable<string>? tags)
    {
        var normalizedPrefix = NormalizeKey(prefix);

        // Anchored at ^ so the Key index can serve the scan instead of falling back to a
        // collection scan.
        var filter = Builders<KeyValueEntry>.Filter.Regex(
            x => x.Key,
            new BsonRegularExpression($"^{Regex.Escape(normalizedPrefix)}"));

        return WithTags(filter, tags);
    }

    private static FilterDefinition<KeyValueEntry> WithTags(FilterDefinition<KeyValueEntry> filter, IEnumerable<string>? tags)
    {
        var normalizedTags = NormalizeTags(tags);
        if (normalizedTags.Count == 0)
        {
            return filter;
        }

        return Builders<KeyValueEntry>.Filter.And(
            filter,
            Builders<KeyValueEntry>.Filter.All(x => x.Tags, normalizedTags));
    }

    private static List<KeyValueItem<T>> ToItems<T>(IEnumerable<KeyValueEntry> entries) =>
        entries.Where(entry => !entry.Value.IsBsonNull).Select(ToItem<T>).ToList();

    private static KeyValueItem<T> ToItem<T>(KeyValueEntry entry) =>
        new(
            entry.ItemId,
            entry.Key,
            Deserialize<T>(entry.Value),
            entry.Tags ?? [],
            entry.CreatedDate,
            entry.LastUpdatedDate,
            entry.CreatedBy,
            entry.LastUpdatedBy,
            entry.OrganizationId);

    private static T Deserialize<T>(BsonValue value) =>
        MongoDB.Bson.Serialization.BsonSerializer.Deserialize<T>(value.ToJson());

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

    private static string NormalizeItemId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }

        return itemId.Trim();
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        tags is null
            ? []
            : tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                  .Select(tag => tag.Trim())
                  .Distinct(StringComparer.Ordinal)
                  .ToList();

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
