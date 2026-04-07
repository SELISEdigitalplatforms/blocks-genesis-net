using Blocks.Genesis;
using Microsoft.Extensions.Caching.Memory;
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
    public void GetDatabase_ByTenant_ShouldResolveDatabaseEachCall_FromCachedClient()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-1"))
            .Returns(new Blocks.Genesis.Tenant
            {
                TenantId = "tenant-1",
                DBName = "tenant_db",
                DbConnectionString = "mongodb://localhost:27017",
                ApplicationDomain = "https://tenant-1.local",
                JwtTokenParameters = new JwtTokenParameters
                {
                    Issuer = "issuer",
                    Subject = "subject",
                    Audiences = [],
                    PublicCertificatePath = "path",
                    PublicCertificatePassword = "password",
                    PrivateCertificatePassword = "private",
                    IssueDate = DateTime.UtcNow
                }
            });

        var provider = CreateProvider(tenants);

        var first = provider.GetDatabase("tenant-1");
        var second = provider.GetDatabase("tenant-1");

        Assert.NotSame(first, second);
        Assert.Equal("tenant_db", first.DatabaseNamespace.DatabaseName);
        Assert.Equal("tenant_db", second.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public void GetDatabase_ByConnectionAndDatabaseName_ShouldResolveByProvidedName()
    {
        var provider = CreateProvider(new Mock<ITenants>());

        var first = provider.GetDatabase("mongodb://localhost:27017", "MainDb");
        var second = provider.GetDatabase("mongodb://localhost:27017", "maindb");

        Assert.Equal("MainDb", first.DatabaseNamespace.DatabaseName);
        Assert.Equal("maindb", second.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public void GetDatabase_ByTenant_ShouldWrapError_WhenTenantConfigMissing()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("missing"))
            .Returns((Blocks.Genesis.Tenant?)null);

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
