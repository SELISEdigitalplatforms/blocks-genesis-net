using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;

namespace XUnitTest.Entities.Genesis;

/// <summary>
/// H9 / I14: a KV value type stored through the real MongoKeyValueStore keeps its class-map bytes as the
/// stored Value (the class bytes themselves are pinned by the per-service golden hashes), and reads back.
/// Needs the throwaway local Mongo on 127.0.0.1:27017, like MongoKeyValueStoreTests.
/// </summary>
[Collection("BlocksAuthStaticState")]
public sealed class KeyValueValueBytesTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";

    public static TheoryData<string> ValueTypes() => new()
    {
        "Blocks.Genesis.Entities.Iam.MfaConfiguration",
        "Blocks.Genesis.Entities.Os.NotificationConfiguration",
        "Blocks.Genesis.Entities.Release.DependencyTrackProjects",
    };

    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(ValueTypes))]
    public async Task SetAsync_StoresTheClassBytesAsValue_AndGetAsyncReadsThem(string packageType)
    {
        var type = EntityCatalog.Package.GetType(packageType, throwOnError: true)!;
        var sample = EntitySamples.Filled(type);

        var client = new MongoClient(MongoConnectionString);
        var dbName = $"genesis-entities-kv-{Guid.NewGuid():N}";
        try
        {
            var db = client.GetDatabase(dbName);
            var dbProvider = new Mock<IDbContextProvider>();
            dbProvider.Setup(p => p.GetDatabase()).Returns(db);
            var secret = new Mock<IBlocksSecret>();
            secret.SetupGet(s => s.DatabaseConnectionString).Returns(MongoConnectionString);
            secret.SetupGet(s => s.RootDatabaseName).Returns(dbName);
            var store = new MongoKeyValueStore(dbProvider.Object, secret.Object);

            var set = typeof(MongoKeyValueStore).GetMethod(nameof(MongoKeyValueStore.SetAsync))!.MakeGenericMethod(type);
            await (Task)set.Invoke(store, ["value.key", sample, true, CancellationToken.None])!;

            var stored = (await db.GetCollection<BsonDocument>("KeyValueStores").Find(new BsonDocument("Key", "value.key")).SingleAsync())["Value"];
            Assert.Equal(sample.ToBsonDocument(type).ToBson(), stored.AsBsonDocument.ToBson());

            var get = typeof(MongoKeyValueStore).GetMethod(nameof(MongoKeyValueStore.GetAsync))!.MakeGenericMethod(type);
            var task = (Task)get.Invoke(store, ["value.key", true, CancellationToken.None])!;
            await task;
            var read = task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.Equal(sample.ToBsonDocument(type), read.ToBsonDocument(type));
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }
}
