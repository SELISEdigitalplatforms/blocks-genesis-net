using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using System.Reflection;
using System.Security.Claims;

namespace XUnitTest.Auth;

/// <summary>
/// Branch coverage for <c>ProtectedEndpointAccessHandler</c>: the MVC filter resource shape,
/// the legacy HttpContext.Items resource name path, missing tenant context, impersonation
/// and explicit organization scoping.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class ProtectedEndpointAccessHandlerCoverageTests : IDisposable
{
    private const string HandlerTypeName = "Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis";
    private const string RequirementTypeName = "Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis";
    private const string ResourceNameItemKey = "ProtectedResourceName";

    public ProtectedEndpointAccessHandlerCoverageTests()
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
    public async Task HandleRequirementAsync_ShouldResolveHttpContext_FromAuthorizationFilterContext()
    {
        BlocksContext.SetContext(CreateContext("tenant-mvc"));
        var dbContext = new Mock<IDbContextProvider>();
        SetupQuota(dbContext, "tenant-mvc", null);
        SetupPermission(dbContext, "tenant-mvc", granted: true);

        var http = new DefaultHttpContext();
        http.Items[ResourceNameItemKey] = "svc::orders::get";
        var filterContext = new AuthorizationFilterContext(
            new Microsoft.AspNetCore.Mvc.ActionContext(http, new RouteData(), new ActionDescriptor()), []);

        var (type, handler) = CreateHandler(dbContext.Object);
        var requirement = CreateRequirement();
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser("svc::orders::get"), filterContext);

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenResourceIsNotHttpBased()
    {
        BlocksContext.SetContext(CreateContext("tenant-plain"));
        var (type, handler) = CreateHandler(new Mock<IDbContextProvider>().Object);
        var requirement = CreateRequirement();
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser(), "not-an-http-resource");

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasFailed);
        Assert.Contains(context.FailureReasons, r => r.Message == "PROTECTED_RESOURCE_REQUIRED");
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenItemsResourceNameIsNull()
    {
        BlocksContext.SetContext(CreateContext("tenant-null-item"));
        var (type, handler) = CreateHandler(new Mock<IDbContextProvider>().Object);
        var requirement = CreateRequirement();

        var http = new DefaultHttpContext();
        http.Items[ResourceNameItemKey] = null;
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser(), http);

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasFailed);
        Assert.Contains(context.FailureReasons, r => r.Message == "PROTECTED_RESOURCE_REQUIRED");
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenUserHasNoIdentity()
    {
        var (type, handler) = CreateHandler(new Mock<IDbContextProvider>().Object);
        var requirement = CreateRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(), new DefaultHttpContext());

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldSkipQuotaAndDenyPermission_WhenTenantContextIsMissing()
    {
        // No BlocksContext at all: the quota check is skipped and the permission
        // check denies access because no tenant can be resolved.
        var dbContext = new Mock<IDbContextProvider>();
        var (type, handler) = CreateHandler(dbContext.Object);
        var requirement = CreateRequirement();

        var http = new DefaultHttpContext();
        http.Items[ResourceNameItemKey] = "svc::orders::get";
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser("svc::orders::get"), http);

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasFailed);
        dbContext.Verify(d => d.GetDatabase(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldUseOriginalTenantAndOrganization_WhenImpersonated()
    {
        BlocksContext.SetContext(BlocksContext.Create(
            "tenant-imp", ["admin"], "user-1", true, "/orders", "org-9", DateTime.MinValue,
            "", [], "", "", "", "", "tenant-orig", "", impersonated: true));

        var dbContext = new Mock<IDbContextProvider>();
        SetupQuota(dbContext, "tenant-imp", null);
        SetupPermission(dbContext, "tenant-orig", granted: true);

        var http = new DefaultHttpContext();
        http.Items[ResourceNameItemKey] = "svc::orders::get";

        var (type, handler) = CreateHandler(dbContext.Object);
        var requirement = CreateRequirement();
        var context = new AuthorizationHandlerContext([requirement], AuthenticatedUser("svc::orders::get"), http);

        await InvokeHandleAsync(type, handler, context, requirement);

        Assert.True(context.HasSucceeded);
        dbContext.Verify(d => d.GetCollection<BsonDocument>("tenant-orig", "Permissions"), Times.Once);
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
