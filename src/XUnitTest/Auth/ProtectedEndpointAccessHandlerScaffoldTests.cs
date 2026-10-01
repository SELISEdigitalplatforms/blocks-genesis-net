using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using System.Reflection;
using System.Security.Claims;

namespace XUnitTest.Auth;

/// <summary>
/// Behaviour tests for the internal <c>ProtectedEndpointAccessHandler</c>. The handler is
/// <c>internal</c>, so it is exercised through reflection. It resolves the required resource
/// name from the endpoint's <see cref="ProtectedEndPointAttribute"/>, enforces a per-tenant
/// request quota, then checks the caller's permissions before succeeding the requirement.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class ProtectedEndpointAccessHandlerScaffoldTests : IDisposable
{
    private const string HandlerTypeName = "Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis";
    private const string RequirementTypeName = "Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis";

    public ProtectedEndpointAccessHandlerScaffoldTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
    }

    [Fact]
    public void InternalType_ShouldExist()
    {
        Assert.NotNull(Type.GetType(HandlerTypeName));
    }

    [Fact]
    public void IsAuthenticated_ShouldReturnFalse_ForAnonymousIdentity()
    {
        var type = Type.GetType(HandlerTypeName)!;
        var method = type.GetMethod("IsAuthenticated", BindingFlags.NonPublic | BindingFlags.Static)!;
        var context = new AuthorizationHandlerContext([], new ClaimsPrincipal(new ClaimsIdentity()), null);

        Assert.False((bool)method.Invoke(null, [context])!);
    }

    [Fact]
    public void IsAuthenticated_ShouldReturnTrue_ForAuthenticatedIdentity()
    {
        var type = Type.GetType(HandlerTypeName)!;
        var method = type.GetMethod("IsAuthenticated", BindingFlags.NonPublic | BindingFlags.Static)!;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "Bearer"));
        var context = new AuthorizationHandlerContext([], principal, null);

        Assert.True((bool)method.Invoke(null, [context])!);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenUserIsNotAuthenticated()
    {
        var (type, handler) = CreateHandler(new Mock<IDbContextProvider>().Object);
        var requirement = CreateRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity()), new DefaultHttpContext());

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenResourceNameMissing()
    {
        BlocksContext.SetContext(CreateContext("tenant-a"));
        var (type, handler) = CreateHandler(new Mock<IDbContextProvider>().Object);
        var requirement = CreateRequirement();
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser(), HttpContextWithResource(null));

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFailWith429_WhenQuotaIsExceeded()
    {
        BlocksContext.SetContext(CreateContext("tenant-a"));
        var dbContext = new Mock<IDbContextProvider>();
        SetupQuota(dbContext, "tenant-a", new BsonDocument { ["Limit"] = 1, ["Usage"] = 1 });

        var (type, handler) = CreateHandler(dbContext.Object);
        var requirement = CreateRequirement();
        var http = HttpContextWithResource("svc::orders::get");
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser("svc::orders::get"), http);

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasFailed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldSucceed_WhenPermissionGranted()
    {
        BlocksContext.SetContext(CreateContext("tenant-std"));
        var dbContext = new Mock<IDbContextProvider>();
        SetupQuota(dbContext, "tenant-std", null);
        SetupPermission(dbContext, "tenant-std", granted: true);

        var (type, handler) = CreateHandler(dbContext.Object);
        var requirement = CreateRequirement();
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser("svc::orders::get"), HttpContextWithResource("svc::orders::get"));

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasSucceeded);
        Assert.False(context.HasFailed);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenPermissionDenied()
    {
        BlocksContext.SetContext(CreateContext("tenant-deny"));
        var dbContext = new Mock<IDbContextProvider>();
        SetupQuota(dbContext, "tenant-deny", null);
        SetupPermission(dbContext, "tenant-deny", granted: false);

        var (type, handler) = CreateHandler(dbContext.Object);
        var requirement = CreateRequirement();
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser("other-resource"), HttpContextWithResource("svc::orders::get"));

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasFailed);
        Assert.False(context.HasSucceeded);
    }

    // ---- helpers ----

    private static (Type Type, object Handler) CreateHandler(IDbContextProvider dbContextProvider)
    {
        var type = Type.GetType(HandlerTypeName)!;
        var handler = Activator.CreateInstance(type, dbContextProvider)!;
        return (type, handler);
    }

    private static IAuthorizationRequirement CreateRequirement()
    {
        var type = Type.GetType(RequirementTypeName)!;
        return (IAuthorizationRequirement)Activator.CreateInstance(type)!;
    }

    private static async Task InvokeHandleAsync(Type type, object handler, AuthorizationHandlerContext context, IAuthorizationRequirement requirement)
    {
        var method = type.GetMethod("HandleRequirementAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(handler, [context, requirement])!;
    }

    private static BlocksContext CreateContext(string tenantId) =>
        BlocksContext.Create(tenantId, ["admin"], "user-1", true, "/orders", "", DateTime.MinValue, "", [], "", "", "", "", tenantId, "");

    private static ClaimsPrincipal AuthenticatedUser(params string[] permissions)
    {
        var claims = new List<Claim> { new(BlocksContext.USER_ID_CLAIM, "user-1") };
        claims.AddRange(permissions.Select(p => new Claim(BlocksContext.PERMISSION_CLAIM, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static DefaultHttpContext HttpContextWithResource(string? resourceName)
    {
        var metadata = resourceName is null
            ? new EndpointMetadataCollection()
            : new EndpointMetadataCollection(new ProtectedEndPointAttribute(resourceName));
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test-endpoint"));
        return http;
    }

    private static void SetupQuota(Mock<IDbContextProvider> dbContext, string tenantId, BsonDocument? limitDocument)
    {
        var database = new Mock<IMongoDatabase>();
        var resourceLimits = new Mock<IMongoCollection<BsonDocument>>();
        resourceLimits
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(limitDocument));
        database
            .Setup(d => d.GetCollection<BsonDocument>("ResourceLimits", It.IsAny<MongoCollectionSettings>()))
            .Returns(resourceLimits.Object);
        dbContext.Setup(d => d.GetDatabase(tenantId)).Returns(database.Object);
    }

    private static void SetupPermission(Mock<IDbContextProvider> dbContext, string tenantId, bool granted)
    {
        var permissions = new Mock<IMongoCollection<BsonDocument>>();
        permissions
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(granted ? new BsonDocument { ["_id"] = ObjectId.GenerateNewId() } : null));
        dbContext.Setup(d => d.GetCollection<BsonDocument>(tenantId, "Permissions")).Returns(permissions.Object);
    }

    private static IAsyncCursor<BsonDocument> CreateCursor(BsonDocument? firstItem)
    {
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        if (firstItem is null)
        {
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>())).Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns(Array.Empty<BsonDocument>());
        }
        else
        {
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>())).Returns(true).Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns([firstItem]);
        }
        return cursor.Object;
    }
}
