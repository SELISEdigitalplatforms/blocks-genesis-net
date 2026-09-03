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
[Collection("BlocksAuthStaticState")]
public sealed class MongoKeyValueStoreTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private const string CollectionName = "KeyValueStores";

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
    public async Task SetAsync_ShouldCreateNonUniqueIndexOnKey()
    {
        await WithStoreAsync(async (store, tenantDatabase, _) =>
        {
            await store.SetAsync("index.check", "value");

            var indexes = await tenantDatabase.GetCollection<BsonDocument>(CollectionName)
                .Indexes.List().ToListAsync();

            var keyIndex = Assert.Single(indexes, index => index["name"].AsString == "KeyValueStores_Key");
            Assert.False(keyIndex.Contains("unique"));
            Assert.DoesNotContain(indexes, index => index["name"].AsString == "keyValueStores_Key_Unique");
        });
    }

    // Databases provisioned by 4.0.6-4.0.9 already carry the unique index, and MongoDB
    // cannot alter one in place. The store has to drop it on first use, otherwise every
    // AddAsync on a pre-existing tenant fails with a duplicate-key error.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetAsync_ShouldDropLegacyUniqueIndex_WhenDatabaseWasProvisionedByAnEarlierVersion()
    {
        await WithStoreAsync(async (store, tenantDatabase, _) =>
        {
            var collection = tenantDatabase.GetCollection<BsonDocument>(CollectionName);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Key"),
                new CreateIndexOptions { Name = "keyValueStores_Key_Unique", Unique = true }));

            await store.SetAsync("legacy.index", "value");

            var indexes = await collection.Indexes.List().ToListAsync();
            Assert.DoesNotContain(indexes, index => index["name"].AsString == "keyValueStores_Key_Unique");
            Assert.Contains(indexes, index => index["name"].AsString == "KeyValueStores_Key");

            // The point of the migration: duplicates are now accepted.
            await store.AddAsync("legacy.index", "second");
            await store.AddAsync("legacy.index", "third");
            Assert.Equal(3, (await store.GetAllAsync<string>("legacy.index")).Count);
        });
    }

    // A database migrated out of band - the camelCase collection renamed to
    // KeyValueStores - keeps its indexes under their old names, because renameCollection
    // does not rewrite them. MongoDB refuses a second index over { Key: 1 } under a new
    // name, so the store has to drop the carried-over one before creating its own.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetAsync_ShouldReplaceKeyIndexCarriedOverFromTheCamelCaseCollection()
    {
        await WithStoreAsync(async (store, tenantDatabase, _) =>
        {
            var collection = tenantDatabase.GetCollection<BsonDocument>(CollectionName);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Key"),
                new CreateIndexOptions { Name = "keyValueStores_Key" }));

            await store.SetAsync("renamed.index", "value");

            var indexes = await collection.Indexes.List().ToListAsync();
            Assert.DoesNotContain(indexes, index => index["name"].AsString == "keyValueStores_Key");
            Assert.Contains(indexes, index => index["name"].AsString == "KeyValueStores_Key");
            Assert.Equal("value", await store.GetAsync<string>("renamed.index"));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AddAsync_ShouldStoreMultipleValuesUnderTheSameKey()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            var firstId = await store.AddAsync("orders.pending", new SampleValue("first", 1));
            var secondId = await store.AddAsync("orders.pending", new SampleValue("second", 2));

            Assert.NotEqual(firstId, secondId);

            var items = await store.GetAllAsync<SampleValue>("orders.pending");

            Assert.Equal(2, items.Count);
            Assert.Contains(items, item => item.ItemId == firstId && item.Value == new SampleValue("first", 1));
            Assert.Contains(items, item => item.ItemId == secondId && item.Value == new SampleValue("second", 2));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AddAsync_ShouldNotOverwriteAnEntryWrittenBySetAsync()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            await store.SetAsync("mixed.key", new SampleValue("from-set", 1));
            await store.AddAsync("mixed.key", new SampleValue("from-add", 2));

            var items = await store.GetAllAsync<SampleValue>("mixed.key");
            Assert.Equal(2, items.Count);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAllAsync_ShouldNarrowByTags()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            await store.AddAsync("audit.entry", new SampleValue("alpha", 1), ["service-a", "critical"]);
            await store.AddAsync("audit.entry", new SampleValue("beta", 2), ["service-b"]);

            var serviceA = await store.GetAllAsync<SampleValue>("audit.entry", ["service-a"]);
            var critical = await store.GetAllAsync<SampleValue>("audit.entry", ["service-a", "critical"]);
            var none = await store.GetAllAsync<SampleValue>("audit.entry", ["service-c"]);

            Assert.Equal(new SampleValue("alpha", 1), Assert.Single(serviceA).Value);
            Assert.Equal(new SampleValue("alpha", 1), Assert.Single(critical).Value);
            Assert.Empty(none);
            Assert.Equal(2, (await store.GetAllAsync<SampleValue>("audit.entry")).Count);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAllByPrefixAsync_ShouldReturnItemsAcrossKeys_AndNarrowByTags()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            await store.AddAsync("cfg.mail.smtp", new SampleValue("smtp", 1), ["mail"]);
            await store.AddAsync("cfg.mail.imap", new SampleValue("imap", 2), ["mail"]);
            await store.AddAsync("cfg.sms.twilio", new SampleValue("twilio", 3), ["sms"]);

            var all = await store.GetAllByPrefixAsync<SampleValue>("cfg.");
            var mailOnly = await store.GetAllByPrefixAsync<SampleValue>("cfg.", ["mail"]);

            Assert.Equal(3, all.Count);
            Assert.All(all, item => Assert.NotEmpty(item.ItemId));
            Assert.Equal(2, mailOnly.Count);
            Assert.All(mailOnly, item => Assert.StartsWith("cfg.mail.", item.Key, StringComparison.Ordinal));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateByIdAsync_ShouldChangeOnlyTheTargetedDocument()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            var firstId = await store.AddAsync("jobs.queued", new SampleValue("first", 1));
            await store.AddAsync("jobs.queued", new SampleValue("second", 2));

            Assert.True(await store.UpdateByIdAsync(firstId, new SampleValue("first-updated", 10)));

            var items = await store.GetAllAsync<SampleValue>("jobs.queued");
            Assert.Equal(2, items.Count);
            Assert.Contains(items, item => item.Value == new SampleValue("first-updated", 10));
            Assert.Contains(items, item => item.Value == new SampleValue("second", 2));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateByIdAsync_ShouldReturnTrue_WhenValueIsUnchanged()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            var itemId = await store.AddAsync("jobs.idempotent", new SampleValue("same", 1));

            // Matched but not modified still means the document is in the requested state.
            Assert.True(await store.UpdateByIdAsync(itemId, new SampleValue("same", 1)));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateByIdAsync_ShouldReturnFalse_WhenItemIdIsUnknown()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            Assert.False(await store.UpdateByIdAsync(ObjectId.GenerateNewId().ToString(), new SampleValue("x", 1)));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetByIdAsync_ShouldReturnItemWithAuditFields_OrNullWhenMissing()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            try
            {
                BlocksContext.SetContext(CreateContext(userId: "user-1", organizationId: "org-9"));
                var itemId = await store.AddAsync("profile.pref", new SampleValue("dark", 1), ["ui"]);

                var item = await store.GetByIdAsync<SampleValue>(itemId);

                Assert.NotNull(item);
                Assert.Equal(itemId, item.ItemId);
                Assert.Equal("profile.pref", item.Key);
                Assert.Equal(new SampleValue("dark", 1), item.Value);
                Assert.Equal(["ui"], item.Tags);
                Assert.Equal("user-1", item.CreatedBy);
                Assert.Equal("org-9", item.OrganizationId);

                Assert.Null(await store.GetByIdAsync<SampleValue>(ObjectId.GenerateNewId().ToString()));
            }
            finally
            {
                BlocksContext.SetContext(null);
            }
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeleteByIdAsync_ShouldRemoveOnlyTheTargetedDocument()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            var firstId = await store.AddAsync("cache.entry", new SampleValue("first", 1));
            await store.AddAsync("cache.entry", new SampleValue("second", 2));

            Assert.True(await store.DeleteByIdAsync(firstId));
            Assert.False(await store.DeleteByIdAsync(firstId));

            var remaining = await store.GetAllAsync<SampleValue>("cache.entry");
            Assert.Equal(new SampleValue("second", 2), Assert.Single(remaining).Value);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeleteAllAsync_ShouldRemoveEveryDocumentUnderTheKey()
    {
        await WithStoreAsync(async (store, _, _) =>
        {
            await store.AddAsync("batch.item", new SampleValue("a", 1));
            await store.AddAsync("batch.item", new SampleValue("b", 2));
            await store.AddAsync("batch.other", new SampleValue("c", 3));

            Assert.Equal(2, await store.DeleteAllAsync("batch.item"));
            Assert.Empty(await store.GetAllAsync<SampleValue>("batch.item"));
            Assert.Single(await store.GetAllAsync<SampleValue>("batch.other"));
            Assert.Equal(0, await store.DeleteAllAsync("batch.item"));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MultiValueApi_ShouldRejectBlankKeysAndIds(string blank)
    {
        var store = new MongoKeyValueStore(new Mock<IDbContextProvider>().Object, new Mock<IBlocksSecret>().Object);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(blank, "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAllAsync<string>(blank));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetByIdAsync<string>(blank));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateByIdAsync(blank, "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteByIdAsync(blank));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAllAsync(blank));
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
