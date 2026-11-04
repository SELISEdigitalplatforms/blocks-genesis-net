using Blocks.Genesis;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;

namespace XUnitTest.Configuration;

public sealed class KeyValueConfigurationTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private const string KeyValueCollection = "KeyValueStores";
    private const string SecretsCollection = "Secrets";
    private const string Key = "blocks-secret-test";

    // ------------------------------------------------------------------ //
    // Registration                                                        //
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("", "db", Key)]
    [InlineData("mongodb://localhost", "", Key)]
    [InlineData("mongodb://localhost", "db", " ")]
    public void AddKeyValueConfiguration_ShouldThrow_WhenARequiredOptionIsEmpty(string connectionString, string databaseName, string key)
    {
        var builder = new ConfigurationBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddKeyValueConfiguration(options =>
        {
            options.ConnectionString = connectionString;
            options.DatabaseName = databaseName;
            options.Key = key;
        }));
    }

    [Fact]
    public void AddKeyValueConfiguration_ShouldDefaultToKeyValueStoresWithSecretsFallback()
    {
        var options = new KeyValueConfigurationOptions();

        Assert.Equal(KeyValueCollection, options.CollectionName);
        Assert.Equal(SecretsCollection, options.FallbackCollectionName);
        Assert.False(options.Optional);
    }

    [Fact]
    public void AddKeyValueConfiguration_ShouldAddASource_FromTheBlocksSecret()
    {
        var secret = new Mock<IBlocksSecret>();
        secret.SetupGet(s => s.DatabaseConnectionString).Returns(MongoConnectionString);
        secret.SetupGet(s => s.RootDatabaseName).Returns("root");
        var builder = new ConfigurationBuilder();

        builder.AddKeyValueConfiguration(secret.Object, Key);

        Assert.IsType<KeyValueConfigurationSource>(Assert.Single(builder.Sources));
    }

    [Fact]
    public void Build_ShouldNotThrow_WhenOptionalAndTheDatabaseIsUnreachable()
    {
        var configuration = new ConfigurationBuilder()
            .AddKeyValueConfiguration(options =>
            {
                options.ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=200&connectTimeoutMS=200";
                options.DatabaseName = "root";
                options.Key = Key;
                options.Optional = true;
            })
            .Build();

        Assert.Empty(configuration.AsEnumerable());
    }

    // ------------------------------------------------------------------ //
    // Flattening                                                          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Flatten_ShouldKeepFlatKeys_AndExpandDocumentsAndArrays()
    {
        var data = KeyValueConfigurationProvider.Flatten(new BsonDocument
        {
            { "FrontendRuntime:BLOCKS_IAM_BASE_URL", "https://iam" },
            { "Ping", new BsonDocument { { "Enabled", true }, { "IntervalInSeconds", 60 } } },
            { "Blocked", new BsonArray { "a.com", "b.com" } },
            { "Missing", BsonNull.Value }
        });

        Assert.Equal("https://iam", data["FrontendRuntime:BLOCKS_IAM_BASE_URL"]);
        Assert.Equal("true", data["Ping:Enabled"]);
        Assert.Equal("60", data["Ping:IntervalInSeconds"]);
        Assert.Equal("a.com", data["Blocked:0"]);
        Assert.Equal("b.com", data["Blocked:1"]);
        Assert.True(data.ContainsKey("Missing"));
        Assert.Null(data["Missing"]);
        Assert.False(data.ContainsKey("Ping"));
        Assert.False(data.ContainsKey("Blocked"));
    }

    [Fact]
    public void Flatten_ShouldBeCaseInsensitive()
    {
        var data = KeyValueConfigurationProvider.Flatten(new BsonDocument { { "RootTenantId", "t1" } });

        Assert.Equal("t1", data["roottenantid"]);
    }

    // ------------------------------------------------------------------ //
    // Loading (real local MongoDB, as in MongoKeyValueStoreTests)         //
    // ------------------------------------------------------------------ //

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldReadKeyValueStores_WhenTheKeyExists()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            await InsertKeyValueAsync(database, Key, new BsonDocument { { "RootTenantId", "from-kv" } });

            var (configuration, provider) = Build(databaseName);

            Assert.Equal("from-kv", configuration["RootTenantId"]);
            Assert.Equal(KeyValueCollection, provider.LoadedFrom);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldIgnoreSecrets_WhenKeyValueStoresHasTheKey()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            await InsertKeyValueAsync(database, Key, new BsonDocument { { "Shared", "from-kv" } });
            await InsertSecretAsync(database, Key, new BsonDocument { { "Shared", "from-secrets" }, { "OnlyInSecrets", "x" } });

            var (configuration, _) = Build(databaseName);

            Assert.Equal("from-kv", configuration["Shared"]);
            Assert.Null(configuration["OnlyInSecrets"]);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldFallBackToSecrets_WhenKeyValueStoresHasNoDocument()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            await InsertKeyValueAsync(database, "blocks-secret-other", new BsonDocument { { "RootTenantId", "other" } });
            await InsertSecretAsync(database, Key, new BsonDocument { { "RootTenantId", "from-secrets" } });

            var (configuration, provider) = Build(databaseName);

            Assert.Equal("from-secrets", configuration["RootTenantId"]);
            Assert.Equal(SecretsCollection, provider.LoadedFrom);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldFallBackToSecrets_WhenTheKeyValueDocumentHasANullValue()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            await InsertKeyValueAsync(database, Key, BsonNull.Value);
            await InsertSecretAsync(database, Key, new BsonDocument { { "RootTenantId", "from-secrets" } });

            var (configuration, _) = Build(databaseName);

            Assert.Equal("from-secrets", configuration["RootTenantId"]);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldThrow_WhenTheKeyValueDocumentHoldsANonDocumentValue()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            await InsertKeyValueAsync(database, Key, "not-a-document");

            var exception = Assert.Throws<InvalidOperationException>(() => Build(databaseName));
            Assert.Contains("must hold a document Value", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldThrow_WhenTheKeyIsInNeitherCollection()
    {
        await WithDatabaseAsync((_, databaseName) =>
        {
            var exception = Assert.Throws<InvalidOperationException>(() => Build(databaseName));
            Assert.Contains(Key, exception.Message, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldYieldNoValues_WhenOptionalAndTheKeyIsInNeitherCollection()
    {
        await WithDatabaseAsync((_, databaseName) =>
        {
            var (configuration, provider) = Build(databaseName, options => options.Optional = true);

            Assert.Empty(configuration.AsEnumerable());
            Assert.Null(provider.LoadedFrom);
            return Task.CompletedTask;
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldNotReadSecrets_WhenTheFallbackIsDisabled()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            await InsertSecretAsync(database, Key, new BsonDocument { { "RootTenantId", "from-secrets" } });

            Assert.Throws<InvalidOperationException>(() => Build(databaseName, options => options.FallbackCollectionName = null));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldYieldNoValues_WhenTheSecretsDocumentHasNoKeyPairs()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            await database.GetCollection<BsonDocument>(SecretsCollection)
                .InsertOneAsync(new BsonDocument { { "_id", Guid.NewGuid().ToString() }, { "SecretKey", Key } });

            var (configuration, provider) = Build(databaseName);

            Assert.Empty(configuration.AsEnumerable());
            Assert.Equal(SecretsCollection, provider.LoadedFrom);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldBindArraysAndSections_FromEitherCollection()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            var values = new BsonDocument
            {
                { "BlockedCustomDeploymentDomain", new BsonArray { "seliseblocks.com", "slsblx.com" } },
                { "PeriodicPingConfiguration", new BsonDocument { { "Enabled", true }, { "PingIntervalInSeconds", 60 } } }
            };
            await InsertSecretAsync(database, Key, values);

            var (configuration, _) = Build(databaseName);

            Assert.Equal(["seliseblocks.com", "slsblx.com"], configuration.GetSection("BlockedCustomDeploymentDomain").Get<string[]>());
            Assert.True(configuration.GetValue<bool>("PeriodicPingConfiguration:Enabled"));
            Assert.Equal(60, configuration.GetValue<int>("PeriodicPingConfiguration:PingIntervalInSeconds"));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Load_ShouldReadAValueWrittenByMongoKeyValueStore()
    {
        await WithDatabaseAsync(async (database, databaseName) =>
        {
            var dbProvider = new Mock<IDbContextProvider>();
            dbProvider.Setup(p => p.GetDatabase(MongoConnectionString, databaseName)).Returns(database);
            var blocksSecret = new Mock<IBlocksSecret>();
            blocksSecret.SetupGet(s => s.DatabaseConnectionString).Returns(MongoConnectionString);
            blocksSecret.SetupGet(s => s.RootDatabaseName).Returns(databaseName);
            var store = new MongoKeyValueStore(dbProvider.Object, blocksSecret.Object);

            await store.SetAsync(Key, new Dictionary<string, string>
            {
                ["FrontendRuntime:BLOCKS_IAM_BASE_URL"] = "https://iam"
            }, impersonated: false);

            var configuration = new ConfigurationBuilder()
                .AddKeyValueConfiguration(blocksSecret.Object, Key)
                .Build();

            Assert.Equal("https://iam", configuration.GetSection("FrontendRuntime")["BLOCKS_IAM_BASE_URL"]);
        });
    }

    // ------------------------------------------------------------------ //
    // Helpers                                                             //
    // ------------------------------------------------------------------ //

    private static (IConfigurationRoot Configuration, KeyValueConfigurationProvider Provider) Build(
        string databaseName,
        Action<KeyValueConfigurationOptions>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddKeyValueConfiguration(options =>
            {
                options.ConnectionString = MongoConnectionString;
                options.DatabaseName = databaseName;
                options.Key = Key;
                configure?.Invoke(options);
            })
            .Build();

        return (configuration, configuration.Providers.OfType<KeyValueConfigurationProvider>().Single());
    }

    private static Task InsertKeyValueAsync(IMongoDatabase database, string key, BsonValue value) =>
        database.GetCollection<BsonDocument>(KeyValueCollection).InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId().ToString() },
            { "Key", key },
            { "Value", value },
            { "Tags", new BsonArray() }
        });

    private static Task InsertSecretAsync(IMongoDatabase database, string secretKey, BsonDocument keyPairs) =>
        database.GetCollection<BsonDocument>(SecretsCollection).InsertOneAsync(new BsonDocument
        {
            { "_id", Guid.NewGuid().ToString() },
            { "SecretKey", secretKey },
            { "KeyPairs", keyPairs }
        });

    private static async Task WithDatabaseAsync(Func<IMongoDatabase, string, Task> testBody)
    {
        var client = new MongoClient(MongoConnectionString);
        var databaseName = $"kv-config-{Guid.NewGuid():N}";

        try
        {
            await testBody(client.GetDatabase(databaseName), databaseName);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }
}
