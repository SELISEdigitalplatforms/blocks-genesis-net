using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.Database;

public class MongoDbContextProviderScaffoldTests
{
    [Fact]
    public void GetDatabase_ShouldThrow_WhenTenantIdIsEmpty()
    {
        var provider = CreateProvider(new Mock<ITenants>());

        Assert.Throws<ArgumentNullException>(() => provider.GetDatabase(string.Empty));
    }

    [Fact]
    public void GetDatabase_NoArg_ShouldReturnNull_WhenBlocksContextHasNoTenant()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
        try
        {
            var provider = CreateProvider(new Mock<ITenants>());

            var db = provider.GetDatabase();

            Assert.Null(db);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = false;
        }
    }

    [Fact]
    public void GetCollection_ShouldThrow_WhenNoTenantContextIsAvailable()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
        try
        {
            var provider = CreateProvider(new Mock<ITenants>());

            Assert.Throws<InvalidOperationException>(() => provider.GetCollection<object>("items"));
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = false;
        }
    }

    [Fact]
    public void GetDatabase_ByTenant_ShouldCacheDatabasePerTenant()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantDatabaseConnectionString("tenant-1"))
            .Returns(("tenant_db", "mongodb://localhost:27017"));

        var provider = CreateProvider(tenants);

        var first = provider.GetDatabase("tenant-1");
        var second = provider.GetDatabase("tenant-1");

        Assert.Same(first, second);
        Assert.Equal("tenant_db", first.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public void GetDatabase_ByConnectionAndDatabaseName_ShouldCacheCaseInsensitiveKey()
    {
        var provider = CreateProvider(new Mock<ITenants>());

        var first = provider.GetDatabase("mongodb://localhost:27017", "MainDb");
        var second = provider.GetDatabase("mongodb://localhost:27017", "maindb");

        Assert.Same(first, second);
    }

    [Fact]
    public void GetDatabase_ByTenant_ShouldWrapError_WhenTenantConfigMissing()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantDatabaseConnectionString("missing"))
            .Returns(((string?)null, (string?)null));

        var provider = CreateProvider(tenants);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetDatabase("missing"));
        Assert.Contains("Could not initialize database", ex.Message);
    }

    private static MongoDbContextProvider CreateProvider(Mock<ITenants> tenants)
    {
        var logger = new Mock<ILogger<MongoDbContextProvider>>();
        return new MongoDbContextProvider(logger.Object, tenants.Object, new System.Diagnostics.ActivitySource("test-mongo"));
    }
}
