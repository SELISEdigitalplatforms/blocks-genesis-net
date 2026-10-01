using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using System.Reflection;

namespace XUnitTest.Middlewares;

[Collection("BlocksAuthStaticState")]
public class OnboardingApiAccessMiddlewareTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";

    [Fact]
    public void Constructor_ShouldThrow_WhenNextIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new OnboardingApiAccessMiddleware(null!, Mock.Of<ITenants>(), Mock.Of<IBlocksSecret>(), Mock.Of<ILogger<OnboardingApiAccessMiddleware>>(), []));

        Assert.Equal("next", ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenTenantsIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new OnboardingApiAccessMiddleware(_ => Task.CompletedTask, null!, Mock.Of<IBlocksSecret>(), Mock.Of<ILogger<OnboardingApiAccessMiddleware>>(), []));

        Assert.Equal("tenants", ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenLoggerIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new OnboardingApiAccessMiddleware(_ => Task.CompletedTask, Mock.Of<ITenants>(), Mock.Of<IBlocksSecret>(), null!, []));

        Assert.Equal("logger", ex.ParamName);
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenNoEndpointIsSet()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out _, out _);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenEndpointIsNeitherControllerNorGraphQL()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out _, out _);
        var context = CreateContextWithEndpoint("/api/thing", "Health check probe");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenBlocksContextIsMissing()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out _, out _);
            var context = CreateContextWithEndpoint("/api/thing", "Sample.Controller.Action");

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldReject_WhenRootTenantAccessesDisallowedApiCrossTenant()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            SetCurrentContext("root-tenant", "other-tenant");

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out var tenants, out var logger);
            tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(MakeTenant("root-tenant", isRootTenant: true));
            SeedAllowedApis(middleware, "api/allowed");
            var context = CreateContextWithEndpoint("/api/private", "Sample.Controller.Action");

            await middleware.InvokeAsync(context);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Blocked cross-tenant")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldReject_WhenRequestPathIsRootAndTenantMismatches()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            SetCurrentContext("root-tenant", "other-tenant");

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out var tenants, out _);
            tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(MakeTenant("root-tenant", isRootTenant: true));
            SeedAllowedApis(middleware, "api/allowed");
            var context = CreateContextWithEndpoint("/", "Sample.Controller.Action");

            await middleware.InvokeAsync(context);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenRequestedApiIsAllowed()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            SetCurrentContext("root-tenant", "other-tenant");

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out var tenants, out _);
            tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(MakeTenant("root-tenant", isRootTenant: true));
            SeedAllowedApis(middleware, "api/allowed");
            var context = CreateContextWithEndpoint("/api/allowed/", "Sample.Controller.Action");

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenTenantIsNotRoot()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            SetCurrentContext("plain-tenant", "other-tenant");

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out var tenants, out _);
            tenants.Setup(t => t.GetTenantByID("plain-tenant")).Returns(MakeTenant("plain-tenant", isRootTenant: false));
            SeedAllowedApis(middleware, "api/allowed");
            var context = CreateContextWithEndpoint("/api/private", "Sample.Controller.Action");

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenTenantLookupReturnsNull()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            SetCurrentContext("ghost-tenant", "other-tenant");

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out var tenants, out _);
            tenants.Setup(t => t.GetTenantByID("ghost-tenant")).Returns((Blocks.Genesis.Tenant?)null);
            SeedAllowedApis(middleware, "api/allowed");
            var context = CreateContextWithEndpoint("/api/private", "Sample.Controller.Action");

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenTenantIdIsEmpty()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            SetCurrentContext(string.Empty, "other-tenant");

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out var tenants, out _);
            SeedAllowedApis(middleware, "api/allowed");
            var context = CreateContextWithEndpoint("/api/private", "Sample.Controller.Action");

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled);
            tenants.Verify(t => t.GetTenantByID(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenOriginalTenantMatches()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            SetCurrentContext("root-tenant", "root-tenant");

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out var tenants, out _);
            tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(MakeTenant("root-tenant", isRootTenant: true));
            SeedAllowedApis(middleware, "api/allowed");
            var context = CreateContextWithEndpoint("/api/private", "Sample.Controller.Action");

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldProceedPastEndpointCheck_WhenDisplayNameIsGraphQL()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out var tenants, out _);
            var context = CreateContextWithEndpoint("/graphql", "GraphQL endpoint");

            await middleware.InvokeAsync(context);

            // Context is missing, so the middleware passes through after the endpoint check.
            Assert.True(nextCalled);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldProceedPastEndpointCheck_WhenDisplayNameIsNull()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();

            var nextCalled = false;
            var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, out _, out _);
            var context = CreateContextWithEndpoint("/api/thing", null);

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvokeAsync_ShouldLoadAllowedApisFromRootDatabase_WhenDocumentHasAllowedApis()
    {
        var databaseName = $"onboarding-mw-tests-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            await client.GetDatabase(databaseName)
                .GetCollection<BsonDocument>("IdentityConfigurations")
                .InsertOneAsync(new BsonDocument
                {
                    { "AllowedApis", new BsonArray { "api/allowed" } }
                });

            var originalTestMode = BlocksContext.IsTestMode;
            try
            {
                BlocksContext.IsTestMode = true;
                SetCurrentContext("root-tenant", "other-tenant");

                var nextCalled = false;
                var middleware = CreateMiddleware(
                    _ => { nextCalled = true; return Task.CompletedTask; },
                    out var tenants,
                    out _,
                    databaseName);
                tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(MakeTenant("root-tenant", isRootTenant: true));
                var context = CreateContextWithEndpoint("/API/Allowed", "Sample.Controller.Action");

                await middleware.InvokeAsync(context);

                // Loaded from the database, matched case-insensitively against the normalized path.
                Assert.True(nextCalled);
            }
            finally
            {
                BlocksContext.ClearContext();
                BlocksContext.IsTestMode = originalTestMode;
            }
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvokeAsync_ShouldFallBackToEmptyAllowedApis_WhenDocumentIsMissing()
    {
        var databaseName = $"onboarding-mw-tests-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            var originalTestMode = BlocksContext.IsTestMode;
            try
            {
                BlocksContext.IsTestMode = true;
                SetCurrentContext("root-tenant", "other-tenant");

                var nextCalled = false;
                var middleware = CreateMiddleware(
                    _ => { nextCalled = true; return Task.CompletedTask; },
                    out var tenants,
                    out _,
                    databaseName);
                tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(MakeTenant("root-tenant", isRootTenant: true));
                var context = CreateContextWithEndpoint("/api/anything", "Sample.Controller.Action");

                await middleware.InvokeAsync(context);

                Assert.False(nextCalled);
                Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            }
            finally
            {
                BlocksContext.ClearContext();
                BlocksContext.IsTestMode = originalTestMode;
            }
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvokeAsync_ShouldFallBackToEmptyAllowedApis_WhenAllowedApisIsNotAnArray()
    {
        var databaseName = $"onboarding-mw-tests-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            await client.GetDatabase(databaseName)
                .GetCollection<BsonDocument>("IdentityConfigurations")
                .InsertOneAsync(new BsonDocument
                {
                    { "AllowedApis", "not-an-array" }
                });

            var originalTestMode = BlocksContext.IsTestMode;
            try
            {
                BlocksContext.IsTestMode = true;
                SetCurrentContext("root-tenant", "other-tenant");

                var nextCalled = false;
                var middleware = CreateMiddleware(
                    _ => { nextCalled = true; return Task.CompletedTask; },
                    out var tenants,
                    out _,
                    databaseName);
                tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(MakeTenant("root-tenant", isRootTenant: true));
                var context = CreateContextWithEndpoint("/api/anything", "Sample.Controller.Action");

                await middleware.InvokeAsync(context);

                Assert.False(nextCalled);
                Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            }
            finally
            {
                BlocksContext.ClearContext();
                BlocksContext.IsTestMode = originalTestMode;
            }
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    private static Blocks.Genesis.Tenant MakeTenant(string tenantId, bool isRootTenant)
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            IsRootTenant = isRootTenant,
            DbConnectionString = MongoConnectionString,
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer", Subject = "subject", Audiences = [],
                PublicCertificatePath = "path", PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty, IssueDate = DateTime.UtcNow
            }
        };
    }

    private static OnboardingApiAccessMiddleware CreateMiddleware(
        RequestDelegate next,
        out Mock<ITenants> tenants,
        out Mock<ILogger<OnboardingApiAccessMiddleware>> logger,
        string? rootDatabaseName = null)
    {
        tenants = new Mock<ITenants>();
        logger = new Mock<ILogger<OnboardingApiAccessMiddleware>>();
        var secret = new Mock<IBlocksSecret>();
        secret.SetupGet(s => s.DatabaseConnectionString).Returns(MongoConnectionString);
        secret.SetupGet(s => s.RootDatabaseName).Returns(rootDatabaseName ?? "unused-root-db");

        return new OnboardingApiAccessMiddleware(next, tenants.Object, secret.Object, logger.Object, []);
    }

    private static DefaultHttpContext CreateContextWithEndpoint(string path, string? displayName)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, displayName));
        return context;
    }

    private static void SetCurrentContext(string tenantId, string originalTenantId)
    {
        BlocksContext.SetContext(BlocksContext.Create(
            tenantId, [], "", true, "", "", DateTime.MinValue, "", [], "", "", "", "", originalTenantId));
    }

    private static void SeedAllowedApis(OnboardingApiAccessMiddleware middleware, params string[] allowedApis)
    {
        var field = typeof(OnboardingApiAccessMiddleware).GetField("_osAllowedApis", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(middleware, allowedApis.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}
