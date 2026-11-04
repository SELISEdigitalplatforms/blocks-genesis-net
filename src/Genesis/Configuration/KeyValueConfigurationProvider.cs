using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis;

/// <summary>
/// Loads one configuration document from the root database at startup.
/// </summary>
/// <remarks>
/// The document is read from <c>KeyValueStores</c> (<c>{ Key, Value: { ... } }</c>). Only when no
/// such document exists is the legacy <c>Secrets</c> collection read
/// (<c>{ SecretKey, KeyPairs: { ... } }</c>); the two are never merged, so once a key is migrated
/// its <c>Secrets</c> document is ignored.
/// <para>
/// Each top-level field becomes a configuration key. Sub-documents and arrays are flattened the
/// standard way (<c>Parent:Child</c>, <c>List:0</c>), so <c>GetSection(..).Get&lt;T&gt;()</c> binds them.
/// </para>
/// </remarks>
public sealed class KeyValueConfigurationProvider : ConfigurationProvider
{
    internal const string LogPrefix = "[Genesis.KeyValueConfiguration]";

    private readonly KeyValueConfigurationOptions _options;

    public KeyValueConfigurationProvider(KeyValueConfigurationOptions options)
    {
        _options = options;
    }

    /// <summary>Which collection the values came from, or <c>null</c> if none had the key.</summary>
    internal string? LoadedFrom { get; private set; }

    public override void Load()
    {
        try
        {
            using var client = new MongoClient(_options.ConnectionString);
            Load(client.GetDatabase(_options.DatabaseName));
        }
        catch (Exception ex) when (_options.Optional)
        {
            Console.Error.WriteLine($"{LogPrefix} Failed to load '{_options.Key}': {ex.Message}");
        }
    }

    internal void Load(IMongoDatabase database)
    {
        var values = ReadKeyValueStore(database);
        if (values is not null)
        {
            LoadedFrom = _options.CollectionName;
        }
        else if (!string.IsNullOrWhiteSpace(_options.FallbackCollectionName))
        {
            values = ReadFallback(database, _options.FallbackCollectionName);
            if (values is not null)
            {
                LoadedFrom = _options.FallbackCollectionName;
                Console.WriteLine($"{LogPrefix} '{_options.Key}' not found in '{_options.CollectionName}'; loaded from fallback '{_options.FallbackCollectionName}'.");
            }
        }

        if (values is null)
        {
            if (!_options.Optional)
                throw new InvalidOperationException(
                    $"{LogPrefix} No document found for '{_options.Key}' in '{_options.CollectionName}'"
                    + (string.IsNullOrWhiteSpace(_options.FallbackCollectionName) ? "." : $" or '{_options.FallbackCollectionName}'."));
            return;
        }

        Data = Flatten(values);
    }

    /// <summary>
    /// The <c>Value</c> document stored under the key, or <c>null</c> if there is none. A null
    /// <c>Value</c> counts as absent, the same as <see cref="IKeyValueStore.GetAsync{T}"/>.
    /// </summary>
    private BsonDocument? ReadKeyValueStore(IMongoDatabase database)
    {
        var document = database.GetCollection<BsonDocument>(_options.CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq(nameof(KeyValueEntry.Key), _options.Key))
            .FirstOrDefault();

        if (document is null
            || !document.TryGetValue(nameof(KeyValueEntry.Value), out var value)
            || value.IsBsonNull)
        {
            return null;
        }

        if (value.BsonType != BsonType.Document)
            throw new InvalidOperationException(
                $"{LogPrefix} '{_options.Key}' in '{_options.CollectionName}' must hold a document Value, found {value.BsonType}.");

        return value.AsBsonDocument;
    }

    /// <summary>
    /// The <c>KeyPairs</c> of the legacy document, or <c>null</c> if there is none. A document
    /// without <c>KeyPairs</c> yields no values, as SeliseBlocks.ConfigurationDriver did.
    /// </summary>
    private BsonDocument? ReadFallback(IMongoDatabase database, string collectionName)
    {
        var document = database.GetCollection<BsonDocument>(collectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("SecretKey", _options.Key))
            .FirstOrDefault();

        if (document is null)
            return null;

        return document.TryGetValue("KeyPairs", out var keyPairs) && keyPairs.BsonType == BsonType.Document
            ? keyPairs.AsBsonDocument
            : new BsonDocument();
    }

    internal static Dictionary<string, string?> Flatten(BsonDocument document)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document)
        {
            AddValue(data, element.Name, element.Value);
        }

        return data;
    }

    private static void AddValue(Dictionary<string, string?> data, string key, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                foreach (var element in value.AsBsonDocument)
                {
                    AddValue(data, ConfigurationPath.Combine(key, element.Name), element.Value);
                }
                break;

            case BsonType.Array:
                var array = value.AsBsonArray;
                for (var i = 0; i < array.Count; i++)
                {
                    AddValue(data, ConfigurationPath.Combine(key, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), array[i]);
                }
                break;

            case BsonType.Null:
                data[key] = null;
                break;

            default:
                data[key] = value.ToString();
                break;
        }
    }
}
