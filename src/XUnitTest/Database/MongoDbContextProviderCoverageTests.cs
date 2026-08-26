using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Moq;
using System.Diagnostics;

namespace XUnitTest.Database;

[Collection("BlocksAuthStaticState")]
public class MongoDbContextProviderCoverageTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private const string AlternateConnectionString = "mongodb://127.0.0.1:27017/?maxPoolSize=99";

    [Fact]
    public void GetDatabase_ShouldReturnCachedInstance_WhenCacheRefreshedWithSameConnectionString()
    {
        var provider = CreateProvider(out _);

        var first = provider.GetDatabase(MongoConnectionString, "CacheDbA");
        var second = provider.GetDatabase(MongoConnectionString, "CacheDbA", isCacheRefreshed: true);

        // IsSameDbConnection compares against the provider's own cached client
        // for the connection string, so an unchanged connection keeps the
        // cached database instance instead of evicting it.
        Assert.Same(first, second);
    }

    [Fact]
    public void GetDatabase_ShouldReplaceCachedInstance_WhenCacheRefreshedAndConnectionDiffers()
    {
        var provider = CreateProvider(out _);

        var first = provider.GetDatabase(MongoConnectionString, "CacheDbB");
        var second = provider.GetDatabase(AlternateConnectionString, "CacheDbB", isCacheRefreshed: true);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void GetDatabase_ShouldCreateInstance_WhenCacheRefreshedButNothingCached()
    {
        var provider = CreateProvider(out _);

        var database = provider.GetDatabase(MongoConnectionString, "CacheDbC", isCacheRefreshed: true);

        Assert.Equal("CacheDbC", database.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public void GetCollection_ShouldReturnCollection_WhenSecurityContextHasTenant()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.SetContext(BlocksContext.Create(
                "ctx-tenant", [], "", true, "", "", DateTime.MinValue, "", [], "", "", "", "", "ctx-tenant"));

            var provider = CreateProvider(out var tenants);
            tenants.Setup(t => t.GetTenantDatabaseConnectionString("ctx-tenant"))
                   .Returns(("ctx-db", MongoConnectionString));

            var collection = provider.GetCollection<BsonDocument>("ctx-collection");

            Assert.Equal("ctx-collection", collection.CollectionNamespace.CollectionName);
            Assert.Equal("ctx-db", collection.Database.DatabaseNamespace.DatabaseName);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    private static MongoDbContextProvider CreateProvider(out Mock<ITenants> tenants)
    {
        tenants = new Mock<ITenants>();
        return new MongoDbContextProvider(
            new Mock<ILogger<MongoDbContextProvider>>().Object,
            tenants.Object,
            new ActivitySource($"MongoDbContextProviderCoverageTests-{Guid.NewGuid():N}"));
    }
}
