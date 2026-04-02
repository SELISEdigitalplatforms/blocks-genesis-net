using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.Database;

public class MongoDbContextProviderAdditionalTests : IDisposable
{
    public MongoDbContextProviderAdditionalTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
        BlocksHttpContextAccessor.Instance = null;
    }

    [Fact]
    public void GetDatabase_ByConnectionString_ShouldThrow_WhenConnectionIsEmpty()
    {
        var provider = CreateProvider(new Mock<ITenants>());

        Assert.Throws<ArgumentNullException>(() => provider.GetDatabase("", "dbName"));
    }

    [Fact]
    public void GetDatabase_ByConnectionString_ShouldThrow_WhenDbNameIsEmpty()
    {
        var provider = CreateProvider(new Mock<ITenants>());

        Assert.Throws<ArgumentNullException>(() => provider.GetDatabase("mongodb://localhost:27017", ""));
    }

    [Fact]
    public void GetDatabase_ByConnectionString_ShouldThrow_WhenBothNull()
    {
        var provider = CreateProvider(new Mock<ITenants>());

        Assert.Throws<ArgumentNullException>(() => provider.GetDatabase(null!, "db"));
    }

    [Fact]
    public void GetCollection_WithTenantId_ShouldReturnCollection()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("t1"))
            .Returns(new Blocks.Genesis.Tenant
            {
                TenantId = "t1",
                DBName = "test_db",
                DbConnectionString = "mongodb://localhost:27017",
                ApplicationDomain = "https://t1.local",
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

        var collection = provider.GetCollection<object>("t1", "items");

        Assert.NotNull(collection);
        Assert.Equal("items", collection.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void GetDatabase_NoArg_ShouldReturnDatabase_WhenTenantContextIsSet()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("context-tenant"))
            .Returns(new Blocks.Genesis.Tenant
            {
                TenantId = "context-tenant",
                DBName = "ctx_db",
                DbConnectionString = "mongodb://localhost:27017",
                ApplicationDomain = "https://context-tenant.local",
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

        var ctx = BlocksContext.Create(
            "context-tenant", [], "", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");
        BlocksContext.SetContext(ctx);

        var provider = CreateProvider(tenants);

        var db = provider.GetDatabase();

        Assert.NotNull(db);
        Assert.Equal("ctx_db", db!.DatabaseNamespace.DatabaseName);
    }

    private static MongoDbContextProvider CreateProvider(Mock<ITenants> tenants)
    {
        var logger = new Mock<ILogger<MongoDbContextProvider>>();
        var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        return new MongoDbContextProvider(logger.Object, tenants.Object, new System.Diagnostics.ActivitySource("test"), memoryCache);
    }
}
