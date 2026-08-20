using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;

namespace XUnitTest.KeyValueStore;

// These tests exercise MongoKeyValueStore against a real local MongoDB instance
// (see docker-compose.yml / the CI "mongodb" service on port 27017) because the
// driver's Find/UpdateOne fluent APIs are impractical to mock with Moq. Only
// IDbContextProvider and IBlocksSecret are mocked, to redirect the "tenant" and
// "root" database lookups at throwaway per-test database names.
public sealed class MongoKeyValueStoreTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private const string CollectionName = "keyValueStores";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetAsync_ThenGetAsync_ShouldRoundTripValue()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            var value = new SampleValue("widget", 3);

            await store.SetAsync("catalog.widget", value);
            var result = await store.GetAsync<SampleValue>("catalog.widget");

            Assert.Equal(value, result);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAsync_ShouldReturnDefault_WhenKeyIsMissing()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            var result = await store.GetAsync<SampleValue>("missing.key");

            Assert.Null(result);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetAsync_ShouldUpdateExistingEntry_WhenKeyAlreadyExists()
    {
        await WithStoreAsync(async (store, tenantDatabase, _) =>
        {
            try
            {
                BlocksContext.SetContext(CreateContext(userId: "user-1", organizationId: "org-1"));
                await store.SetAsync("settings.retry", new SampleValue("first", 1));

                BlocksContext.SetContext(CreateContext(userId: "user-2", organizationId: "org-1"));
                await store.SetAsync("settings.retry", new SampleValue("second", 2));

                var result = await store.GetAsync<SampleValue>("settings.retry");
                Assert.Equal(new SampleValue("second", 2), result);

                // SetOnInsert fields (CreatedBy) must survive the update; only the
                // Set fields (LastUpdatedBy) should reflect the second call.
                var raw = await RawEntryAsync(tenantDatabase, "settings.retry");
                Assert.Equal("user-1", raw["CreatedBy"].AsString);
                Assert.Equal("user-2", raw["LastUpdatedBy"].AsString);
            }
            finally
            {
                BlocksContext.SetContext(null);
            }
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetAsync_ShouldDefaultOrganizationId_AndLeaveCreatedByNull_WhenNoContextIsSet()
    {
        await WithStoreAsync(async (store, tenantDatabase, _) =>
        {
            BlocksContext.SetContext(null);

            await store.SetAsync("no.context", "value");

            var raw = await RawEntryAsync(tenantDatabase, "no.context");
            Assert.Equal("default", raw["OrganizationId"].AsString);
            Assert.True(raw["CreatedBy"].IsBsonNull);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeleteAsync_ShouldRemoveEntry_AndReturnFalseWhenAlreadyGone()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            await store.SetAsync("cache.token", "abc123");
            Assert.True(await store.ExistsAsync("cache.token"));

            var firstDelete = await store.DeleteAsync("cache.token");
            var secondDelete = await store.DeleteAsync("cache.token");

            Assert.True(firstDelete);
            Assert.False(secondDelete);
            Assert.False(await store.ExistsAsync("cache.token"));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetByPrefixAsync_ShouldReturnOnlyMatchingEntries_AndTreatDotsLiterally()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            await store.SetAsync("email.sender", new SampleValue("sender", 1));
            await store.SetAsync("email.retry", new SampleValue("retry", 2));
            // If the prefix were compiled into an unescaped regex, "." would mean
            // "any character" and this key would wrongly match "email.".
            await store.SetAsync("emailx.other", new SampleValue("other", 3));
            await store.SetAsync("sms.sender", new SampleValue("sms", 4));

            var results = await store.GetByPrefixAsync<SampleValue>("email.");

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r == new SampleValue("sender", 1));
            Assert.Contains(results, r => r == new SampleValue("retry", 2));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetByPrefixAsync_ShouldReturnEmptyList_WhenNoKeyMatches()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            await store.SetAsync("other.key", "value");

            var results = await store.GetByPrefixAsync<string>("nomatch.");

            Assert.Empty(results);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetAsync_ShouldCreateUniqueIndexOnKey()
    {
        await WithStoreAsync(async (store, tenantDatabase, _) =>
        {
            await store.SetAsync("index.check", "value");

            var indexes = await tenantDatabase.GetCollection<BsonDocument>(CollectionName)
                .Indexes.List().ToListAsync();

            Assert.Contains(indexes, index =>
                index["name"].AsString == "keyValueStores_Key_Unique" &&
                index["unique"].AsBoolean);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetAsync_WithImpersonatedFalse_ShouldWriteToRootDatabase_NotTenantDatabase()
    {
        await WithStoreAsync(async (store, tenantDatabase, rootDatabase) =>
        {
            await store.SetAsync("global.flag", true, impersonated: false);

            Assert.True(await store.GetAsync<bool>("global.flag", impersonated: false));
            // Same key, tenant database: nothing was written there, so this falls
            // back to default(bool), which is false.
            Assert.False(await store.GetAsync<bool>("global.flag", impersonated: true));

            var rootCount = await rootDatabase.GetCollection<BsonDocument>(CollectionName)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            var tenantCount = await tenantDatabase.GetCollection<BsonDocument>(CollectionName)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

            Assert.Equal(1, rootCount);
            Assert.Equal(0, tenantCount);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAsync_ShouldRouteToTenantOrRootDatabase_BasedOnImpersonatedFlag()
    {
        var client = new MongoClient(MongoConnectionString);
        var tenantDbName = $"kv-route-tenant-{Guid.NewGuid():N}";
        var rootDbName = $"kv-route-root-{Guid.NewGuid():N}";

        try
        {
            var dbProvider = new Mock<IDbContextProvider>();
            dbProvider.Setup(p => p.GetDatabase()).Returns(client.GetDatabase(tenantDbName));
            dbProvider.Setup(p => p.GetDatabase(MongoConnectionString, rootDbName)).Returns(client.GetDatabase(rootDbName));

            var blocksSecret = new Mock<IBlocksSecret>();
            blocksSecret.SetupGet(s => s.DatabaseConnectionString).Returns(MongoConnectionString);
            blocksSecret.SetupGet(s => s.RootDatabaseName).Returns(rootDbName);

            var store = new MongoKeyValueStore(dbProvider.Object, blocksSecret.Object);

            await store.GetAsync<string>("any.key");
            dbProvider.Verify(p => p.GetDatabase(), Times.Once);
            dbProvider.Verify(p => p.GetDatabase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);

            await store.GetAsync<string>("any.key", impersonated: false);
            dbProvider.Verify(p => p.GetDatabase(MongoConnectionString, rootDbName), Times.Once);
            dbProvider.Verify(p => p.GetDatabase(), Times.Once);
        }
        finally
        {
            await client.DropDatabaseAsync(tenantDbName);
            await client.DropDatabaseAsync(rootDbName);
        }
    }

    private static async Task<BsonDocument> RawEntryAsync(IMongoDatabase database, string key)
    {
        return await database.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("Key", key))
            .FirstOrDefaultAsync();
    }

    private static BlocksContext CreateContext(string userId, string organizationId) =>
        BlocksContext.Create(
            tenantId: "tenant-1",
            roles: [],
            userId: userId,
            isAuthenticated: true,
            requestUri: "/",
            organizationId: organizationId,
            expireOn: DateTime.UtcNow.AddHours(1),
            email: "",
            permissions: [],
            userName: "",
            phoneNumber: "",
            displayName: "",
            oauthToken: "",
            originalTenantId: "tenant-1");

    private static async Task WithStoreAsync(Func<MongoKeyValueStore, IMongoDatabase, IMongoDatabase, Task> testBody)
    {
        var client = new MongoClient(MongoConnectionString);
        var tenantDbName = $"kv-store-tenant-{Guid.NewGuid():N}";
        var rootDbName = $"kv-store-root-{Guid.NewGuid():N}";

        try
        {
            var tenantDatabase = client.GetDatabase(tenantDbName);
            var rootDatabase = client.GetDatabase(rootDbName);

            var dbProvider = new Mock<IDbContextProvider>();
            dbProvider.Setup(p => p.GetDatabase()).Returns(tenantDatabase);
            dbProvider.Setup(p => p.GetDatabase(MongoConnectionString, rootDbName)).Returns(rootDatabase);

            var blocksSecret = new Mock<IBlocksSecret>();
            blocksSecret.SetupGet(s => s.DatabaseConnectionString).Returns(MongoConnectionString);
            blocksSecret.SetupGet(s => s.RootDatabaseName).Returns(rootDbName);

            var store = new MongoKeyValueStore(dbProvider.Object, blocksSecret.Object);

            await testBody(store, tenantDatabase, rootDatabase);
        }
        finally
        {
            await client.DropDatabaseAsync(tenantDbName);
            await client.DropDatabaseAsync(rootDbName);
        }
    }

    private sealed record SampleValue(string Name, int Count);
}
