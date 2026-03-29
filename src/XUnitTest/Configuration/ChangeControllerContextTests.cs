using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using OpenTelemetry;

namespace XUnitTest.Configuration;

public class ChangeControllerContextTests : IDisposable
{
    public ChangeControllerContextTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
        Baggage.SetBaggage("TenantId", null);
        Baggage.SetBaggage("ActualTenantId", null);
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
        Baggage.SetBaggage("TenantId", null);
        Baggage.SetBaggage("ActualTenantId", null);
    }

    [Fact]
    public void ChangeContext_ShouldReturnEarly_WhenProjectKeyIsEmpty()
    {
        var tenants = new Mock<ITenants>(MockBehavior.Strict);
        var dbContextProvider = new Mock<IDbContextProvider>(MockBehavior.Strict);
        var sut = new ChangeControllerContext(tenants.Object, dbContextProvider.Object, Mock.Of<IHttpContextAccessor>());
        BlocksContext.SetContext(CreateContext("root-tenant", "user-1"));

        sut.ChangeContext(new ProjectKeyStub { ProjectKey = string.Empty });

        Assert.Equal("root-tenant", BlocksContext.GetContext()?.TenantId);
        Assert.Equal("root-tenant", Baggage.GetBaggage("ActualTenantId"));
        tenants.VerifyNoOtherCalls();
        dbContextProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public void ChangeContext_ShouldHandleMissingCurrentContext_WhenProjectKeyIsEmpty()
    {
        var tenants = new Mock<ITenants>(MockBehavior.Strict);
        var dbContextProvider = new Mock<IDbContextProvider>(MockBehavior.Strict);
        var sut = new ChangeControllerContext(tenants.Object, dbContextProvider.Object, Mock.Of<IHttpContextAccessor>());
        BlocksContext.ClearContext();

        sut.ChangeContext(new ProjectKeyStub { ProjectKey = "" });

        var actualTenantId = Baggage.GetBaggage("ActualTenantId");
        Assert.True(string.IsNullOrEmpty(actualTenantId));
        tenants.VerifyNoOtherCalls();
        dbContextProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public void ChangeContext_ShouldReturnEarly_WhenProjectKeyEqualsCurrentTenant()
    {
        var tenants = new Mock<ITenants>(MockBehavior.Strict);
        var dbContextProvider = new Mock<IDbContextProvider>(MockBehavior.Strict);
        var sut = new ChangeControllerContext(tenants.Object, dbContextProvider.Object, Mock.Of<IHttpContextAccessor>());
        BlocksContext.SetContext(CreateContext("tenant-a", "user-1"));

        sut.ChangeContext(new ProjectKeyStub { ProjectKey = "tenant-a" });

        Assert.Equal("tenant-a", BlocksContext.GetContext()?.TenantId);
        Assert.Equal("tenant-a", Baggage.GetBaggage("ActualTenantId"));
        tenants.VerifyNoOtherCalls();
        dbContextProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public void ChangeContext_ShouldNotSwitch_WhenCurrentTenantIsNotRoot()
    {
        var tenants = new Mock<ITenants>();
        var dbContextProvider = new Mock<IDbContextProvider>();

        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateCursorWithFirstDocument(new BsonDocument("ok", 1)).Object);

        dbContextProvider.Setup(d => d.GetCollection<BsonDocument>("ProjectPeoples")).Returns(collection.Object);

        tenants.Setup(t => t.GetTenantByID("project-2")).Returns(CreateTenant("other-owner", isRoot: false));
        tenants.Setup(t => t.GetTenantByID("tenant-a")).Returns(CreateTenant("owner", isRoot: false));

        var sut = new ChangeControllerContext(tenants.Object, dbContextProvider.Object, Mock.Of<IHttpContextAccessor>());
        BlocksContext.SetContext(CreateContext("tenant-a", "user-1"));

        sut.ChangeContext(new ProjectKeyStub { ProjectKey = "project-2" });

        Assert.Equal("tenant-a", BlocksContext.GetContext()?.TenantId);
        Assert.Null(Baggage.GetBaggage("TenantId"));
    }

    [Fact]
    public void ChangeContext_ShouldSwitch_WhenRootAndUserIsTenantCreator()
    {
        var tenants = new Mock<ITenants>();
        var dbContextProvider = new Mock<IDbContextProvider>();

        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateCursorWithFirstDocument(null).Object);

        dbContextProvider.Setup(d => d.GetCollection<BsonDocument>("ProjectPeoples")).Returns(collection.Object);

        tenants.Setup(t => t.GetTenantByID("project-2")).Returns(CreateTenant("user-1", isRoot: false));
        tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(CreateTenant("root-owner", isRoot: true));

        var sut = new ChangeControllerContext(tenants.Object, dbContextProvider.Object, Mock.Of<IHttpContextAccessor>());
        var original = CreateContext("root-tenant", "user-1");
        BlocksContext.SetContext(original);

        sut.ChangeContext(new ProjectKeyStub { ProjectKey = "project-2" });

        var changed = BlocksContext.GetContext();
        Assert.Equal("project-2", changed?.TenantId);
        Assert.Equal("user-1", changed?.UserId);
        Assert.Equal("root-tenant", changed?.ActualTenantId);
        Assert.Equal("root-tenant", Baggage.GetBaggage("ActualTenantId"));
        Assert.Equal("project-2", Baggage.GetBaggage("TenantId"));
    }

    [Fact]
    public void ChangeContext_ShouldSwitch_WhenRootAndProjectIsShared()
    {
        var tenants = new Mock<ITenants>();
        var dbContextProvider = new Mock<IDbContextProvider>();

        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateCursorWithFirstDocument(new BsonDocument("UserId", "user-1")).Object);

        dbContextProvider.Setup(d => d.GetCollection<BsonDocument>("ProjectPeoples")).Returns(collection.Object);

        tenants.Setup(t => t.GetTenantByID("project-2")).Returns(CreateTenant("someone-else", isRoot: false));
        tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(CreateTenant("root-owner", isRoot: true));

        var sut = new ChangeControllerContext(tenants.Object, dbContextProvider.Object, Mock.Of<IHttpContextAccessor>());
        BlocksContext.SetContext(CreateContext("root-tenant", "user-1"));

        sut.ChangeContext(new ProjectKeyStub { ProjectKey = "project-2" });

        Assert.Equal("project-2", BlocksContext.GetContext()?.TenantId);
        Assert.Equal("project-2", Baggage.GetBaggage("TenantId"));
    }

    [Fact]
    public void ChangeContext_ShouldSwitch_WhenRootAndTargetTenantMissingButProjectIsShared()
    {
        var tenants = new Mock<ITenants>();
        var dbContextProvider = new Mock<IDbContextProvider>();

        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateCursorWithFirstDocument(new BsonDocument("UserId", "user-1")).Object);

        dbContextProvider.Setup(d => d.GetCollection<BsonDocument>("ProjectPeoples")).Returns(collection.Object);

        tenants.Setup(t => t.GetTenantByID("project-2")).Returns((Blocks.Genesis.Tenant?)null);
        tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(CreateTenant("root-owner", isRoot: true));

        var sut = new ChangeControllerContext(tenants.Object, dbContextProvider.Object, Mock.Of<IHttpContextAccessor>());
        BlocksContext.SetContext(CreateContext("root-tenant", "user-1"));

        sut.ChangeContext(new ProjectKeyStub { ProjectKey = "project-2" });

        Assert.Equal("project-2", BlocksContext.GetContext()?.TenantId);
        Assert.Equal("project-2", Baggage.GetBaggage("TenantId"));
    }

    [Fact]
    public void ChangeContext_ShouldNotSwitch_WhenRootButNoAccess()
    {
        var tenants = new Mock<ITenants>();
        var dbContextProvider = new Mock<IDbContextProvider>();

        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateCursorWithFirstDocument(null).Object);

        dbContextProvider.Setup(d => d.GetCollection<BsonDocument>("ProjectPeoples")).Returns(collection.Object);

        tenants.Setup(t => t.GetTenantByID("project-2")).Returns(CreateTenant("another-user", isRoot: false));
        tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(CreateTenant("root-owner", isRoot: true));

        var sut = new ChangeControllerContext(tenants.Object, dbContextProvider.Object, Mock.Of<IHttpContextAccessor>());
        BlocksContext.SetContext(CreateContext("root-tenant", "user-1"));

        sut.ChangeContext(new ProjectKeyStub { ProjectKey = "project-2" });

        Assert.Equal("root-tenant", BlocksContext.GetContext()?.TenantId);
        Assert.Null(Baggage.GetBaggage("TenantId"));
    }

    private static Mock<IAsyncCursor<BsonDocument>> CreateCursorWithFirstDocument(BsonDocument? firstDocument)
    {
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        var documents = firstDocument == null ? Array.Empty<BsonDocument>() : new[] { firstDocument };

        cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>()))
            .Returns(true)
            .Returns(false);
        cursor.SetupGet(c => c.Current).Returns(documents);
        return cursor;
    }

    private static BlocksContext CreateContext(string tenantId, string userId)
    {
        return BlocksContext.Create(
            tenantId: tenantId,
            roles: ["admin"],
            userId: userId,
            isAuthenticated: true,
            requestUri: "/api/test",
            organizationId: "org-1",
            expireOn: DateTime.UtcNow.AddMinutes(30),
            email: "user@example.com",
            permissions: ["p.read"],
            userName: "test-user",
            phoneNumber: "000",
            displayName: "Test User",
            oauthToken: "token",
            refreshToken: "refresh",    
            actualTentId: tenantId);
    }

    private static Blocks.Genesis.Tenant CreateTenant(string? createdBy, bool isRoot)
    {
        return new Blocks.Genesis.Tenant
        {
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = ["aud"],
                PublicCertificatePath = "path",
                PublicCertificatePassword = "pwd",
                PrivateCertificatePassword = "private",
                IssueDate = DateTime.UtcNow
            },
            CreatedBy = createdBy,
            IsRootTenant = isRoot
        };
    }

    private sealed class ProjectKeyStub : IProjectKey
    {
        public string ProjectKey { get; set; } = string.Empty;
    }
}