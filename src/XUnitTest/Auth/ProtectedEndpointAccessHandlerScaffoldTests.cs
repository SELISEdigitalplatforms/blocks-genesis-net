using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using System.Reflection;
using System.Security.Claims;
using System.Text;

namespace XUnitTest.Auth;

public class ProtectedEndpointAccessHandlerScaffoldTests
{
    [Fact]
    public void InternalType_ShouldExist()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);
    }

    [Fact]
    public void IsAuthenticated_ShouldReturnFalse_ForAnonymousIdentity()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var context = new AuthorizationHandlerContext([], new ClaimsPrincipal(new ClaimsIdentity()), null);
        var method = type!.GetMethod("IsAuthenticated", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, [context])!;
        Assert.False(result);
    }

    [Fact]
    public void IsAuthenticated_ShouldReturnTrue_ForAuthenticatedIdentity()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")], "Bearer"));
        var context = new AuthorizationHandlerContext([], principal, null);
        var method = type!.GetMethod("IsAuthenticated", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, [context])!;
        Assert.True(result);
    }

    [Fact]
    public async Task ExtractProjectKeyAsync_ShouldReadQueryValue_WhenTenantIsRoot()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var dbContext = new Mock<IDbContextProvider>();
        var blocksSecret = new Mock<IBlocksSecret>();
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(CreateRootTenant("root-tenant"));

        var instance = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        Assert.NotNull(instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[BlocksConstants.BlocksKey] = "root-tenant";
        httpContext.Request.QueryString = new QueryString("?ProjectKey=project-42");

        var method = type!.GetMethod("ExtractProjectKeyAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<string?>)method!.Invoke(instance, [httpContext])!;
        var result = await task;

        Assert.Equal("project-42", result);
    }

    [Fact]
    public async Task ExtractProjectKeyAsync_ShouldReadBodyValue_WhenRootTenantAndNoQuery()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var dbContext = new Mock<IDbContextProvider>();
        var blocksSecret = new Mock<IBlocksSecret>();
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(CreateRootTenant("root-tenant"));

        var instance = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        Assert.NotNull(instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[BlocksConstants.BlocksKey] = "root-tenant";
        httpContext.Request.ContentType = "application/json";
        var body = "{\"projectKey\":\"project-from-body\"}";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        httpContext.Request.ContentLength = body.Length;

        var method = type!.GetMethod("ExtractProjectKeyAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<string?>)method!.Invoke(instance, [httpContext])!;
        var result = await task;

        Assert.Equal("project-from-body", result);
    }

    [Fact]
    public async Task ExtractProjectKeyAsync_ShouldReturnNull_WhenTenantIsNotRoot()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var dbContext = new Mock<IDbContextProvider>();
        var blocksSecret = new Mock<IBlocksSecret>();
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-a")).Returns(CreateNonRootTenant("tenant-a"));

        var instance = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        Assert.NotNull(instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[BlocksConstants.BlocksKey] = "tenant-a";
        httpContext.Request.QueryString = new QueryString("?ProjectKey=ignored");

        var method = type!.GetMethod("ExtractProjectKeyAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<string?>)method!.Invoke(instance, [httpContext])!;
        var result = await task;

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractProjectKeyAsync_ShouldReturnNull_ForInvalidJsonBody()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var dbContext = new Mock<IDbContextProvider>();
        var blocksSecret = new Mock<IBlocksSecret>();
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(CreateRootTenant("root-tenant"));

        var instance = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        Assert.NotNull(instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[BlocksConstants.BlocksKey] = "root-tenant";
        httpContext.Request.ContentType = "application/json";
        var body = "{invalid-json";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        httpContext.Request.ContentLength = body.Length;

        var method = type!.GetMethod("ExtractProjectKeyAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<string?>)method!.Invoke(instance, [httpContext])!;
        var result = await task;

        Assert.Null(result);
    }

    [Fact]
    public void GetActionAndController_ShouldReadFromEndpointMetadata()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var httpContext = new DefaultHttpContext();
        var descriptor = new ControllerActionDescriptor
        {
            ActionName = "GetOrders",
            ControllerName = "Orders"
        };
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(descriptor), "test");
        httpContext.SetEndpoint(endpoint);

        var context = new AuthorizationHandlerContext([], new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer")), httpContext);
        var method = type!.GetMethod("GetActionAndController", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var tuple = ((string Action, string Controller))method!.Invoke(null, [context])!;
        Assert.Equal("GetOrders", tuple.Action);
        Assert.Equal("Orders", tuple.Controller);
    }

    [Fact]
    public void GetActionName_ShouldReturnEmpty_WhenResourceIsNotHttpContext()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var context = new AuthorizationHandlerContext([], new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer")), new object());
        var method = type!.GetMethod("GetActionName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, [context])!;
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetControllerName_ShouldReturnEmpty_WhenResourceIsNotHttpContext()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var context = new AuthorizationHandlerContext([], new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer")), new object());
        var method = type!.GetMethod("GetControllerName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, [context])!;
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenUserIsNotAuthenticated()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        var requirementType = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis");
        Assert.NotNull(type);
        Assert.NotNull(requirementType);

        var dbContext = new Mock<IDbContextProvider>();
        var blocksSecret = new Mock<IBlocksSecret>();
        var tenants = new Mock<ITenants>();
        var instance = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        Assert.NotNull(instance);

        var requirement = Activator.CreateInstance(requirementType!);
        var context = new AuthorizationHandlerContext([(IAuthorizationRequirement)requirement!], new ClaimsPrincipal(new ClaimsIdentity()), new DefaultHttpContext());

        var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(instance, [context, requirement!])!;
        await task;

        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFailWith429_WhenQuotaIsExceeded()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(BlocksContext.Create("tenant-a", [], "user-1", true, "/orders", "", DateTime.MinValue, "", [], "", "", "", "", "", "tenant-a"));

        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        var requirementType = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis");
        Assert.NotNull(type);
        Assert.NotNull(requirementType);

        var dbContext = new Mock<IDbContextProvider>();
        var database = new Mock<IMongoDatabase>();
        var resourceLimits = new Mock<IMongoCollection<BsonDocument>>();
        var quotaExceededDoc = new BsonDocument { ["Limit"] = 1, ["Usage"] = 1 };
        resourceLimits
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(quotaExceededDoc));

        database
            .Setup(d => d.GetCollection<BsonDocument>("ResourceLimits", It.IsAny<MongoCollectionSettings>()))
            .Returns(resourceLimits.Object);
        dbContext.Setup(d => d.GetDatabase("tenant-a")).Returns(database.Object);

        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(s => s.ServiceName).Returns("svc");

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-a")).Returns(CreateNonRootTenant("tenant-a"));

        var handler = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        var requirement = (IAuthorizationRequirement)Activator.CreateInstance(requirementType!)!;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(BlocksContext.USER_ID_CLAIM, "user-1"),
            new Claim(BlocksContext.PERMISSION_CLAIM, "svc::orders::get")
        ], "Bearer"));

        var http = new DefaultHttpContext();
        var endpoint = new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor { ActionName = "Get", ControllerName = "Orders" }),
            "orders-get");
        http.SetEndpoint(endpoint);

        var context = new AuthorizationHandlerContext([requirement], principal, http);

        var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(handler, [context, requirement])!;
        await task;

        Assert.True(context.HasFailed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldSucceed_ForRootTenantProjectOwner()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(BlocksContext.Create("root-tenant", [], "user-1", true, "/orders", "", DateTime.MinValue, "", [], "", "", "", "", "", "root-tenant"));

        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        var requirementType = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis");
        Assert.NotNull(type);
        Assert.NotNull(requirementType);

        var dbContext = new Mock<IDbContextProvider>();
        var database = new Mock<IMongoDatabase>();
        var resourceLimits = new Mock<IMongoCollection<BsonDocument>>();
        resourceLimits
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(null));

        database
            .Setup(d => d.GetCollection<BsonDocument>("ResourceLimits", It.IsAny<MongoCollectionSettings>()))
            .Returns(resourceLimits.Object);
        dbContext.Setup(d => d.GetDatabase("project-1")).Returns(database.Object);

        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(s => s.ServiceName).Returns("svc");

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("root-tenant")).Returns(CreateRootTenant("root-tenant"));
        tenants.Setup(t => t.GetTenantByID("project-1")).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = "project-1",
            CreatedBy = "user-1",
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = [],
                PublicCertificatePath = "path",
                PublicCertificatePassword = "pw",
                PrivateCertificatePassword = "private",
                IssueDate = DateTime.UtcNow
            }
        });

        var handler = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        var requirement = (IAuthorizationRequirement)Activator.CreateInstance(requirementType!)!;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(BlocksContext.USER_ID_CLAIM, "user-1"),
            new Claim(BlocksContext.PERMISSION_CLAIM, "svc::orders::get")
        ], "Bearer"));

        var http = new DefaultHttpContext();
        http.Request.Headers[BlocksConstants.BlocksKey] = "root-tenant";
        http.Request.QueryString = new QueryString("?ProjectKey=project-1");
        var endpoint = new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor { ActionName = "Get", ControllerName = "Orders" }),
            "orders-get");
        http.SetEndpoint(endpoint);

        var context = new AuthorizationHandlerContext([requirement], principal, http);

        var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(handler, [context, requirement])!;
        await task;

        Assert.True(context.HasSucceeded);
        Assert.False(context.HasFailed);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldSucceed_ForStandardPermissionCheck()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(BlocksContext.Create("tenant-std", ["admin"], "user-1", true, "/orders", "", DateTime.MinValue, "", [], "", "", "", "", "", "tenant-std"));

        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        var requirementType = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis");
        Assert.NotNull(type);
        Assert.NotNull(requirementType);

        var dbContext = new Mock<IDbContextProvider>();
        var database = new Mock<IMongoDatabase>();
        var resourceLimits = new Mock<IMongoCollection<BsonDocument>>();
        resourceLimits
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(null));

        database
            .Setup(d => d.GetCollection<BsonDocument>("ResourceLimits", It.IsAny<MongoCollectionSettings>()))
            .Returns(resourceLimits.Object);
        dbContext.Setup(d => d.GetDatabase("tenant-std")).Returns(database.Object);

        var permissionsCollection = new Mock<IMongoCollection<BsonDocument>>();
        permissionsCollection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        dbContext.Setup(d => d.GetCollection<BsonDocument>("Permissions")).Returns(permissionsCollection.Object);

        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(s => s.ServiceName).Returns("svc");

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-std")).Returns(CreateNonRootTenant("tenant-std"));

        var handler = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        var requirement = (IAuthorizationRequirement)Activator.CreateInstance(requirementType!)!;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(BlocksContext.USER_ID_CLAIM, "user-1"),
            new Claim(BlocksContext.PERMISSION_CLAIM, "svc::orders::get")
        ], "Bearer"));

        var http = new DefaultHttpContext();
        http.Request.Headers[BlocksConstants.BlocksKey] = "tenant-std";
        var endpoint = new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor { ActionName = "Get", ControllerName = "Orders" }),
            "orders-get");
        http.SetEndpoint(endpoint);

        var context = new AuthorizationHandlerContext([requirement], principal, http);

        var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(handler, [context, requirement])!;
        await task;

        Assert.True(context.HasSucceeded);
        Assert.False(context.HasFailed);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenActionOrControllerMissing()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(BlocksContext.Create("tenant-a", [], "user-1", true, "/", "", DateTime.MinValue, "", [], "", "", "", "", "", "tenant-a"));

        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        var requirementType = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis");
        Assert.NotNull(type);
        Assert.NotNull(requirementType);

        var dbContext = new Mock<IDbContextProvider>();
        var blocksSecret = new Mock<IBlocksSecret>();
        var tenants = new Mock<ITenants>();
        var handler = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        var requirement = (IAuthorizationRequirement)Activator.CreateInstance(requirementType!)!;

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(BlocksContext.USER_ID_CLAIM, "user-1")], "Bearer"));
        var context = new AuthorizationHandlerContext([requirement], principal, new DefaultHttpContext());

        var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(handler, [context, requirement])!;
        await task;

        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenStandardPermissionCheckReturnsFalse()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(BlocksContext.Create("tenant-deny", ["viewer"], "user-1", true, "/orders", "", DateTime.MinValue, "", [], "", "", "", "", "", "tenant-deny"));

        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        var requirementType = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis");
        Assert.NotNull(type);
        Assert.NotNull(requirementType);

        var dbContext = new Mock<IDbContextProvider>();

        var database = new Mock<IMongoDatabase>();
        var resourceLimits = new Mock<IMongoCollection<BsonDocument>>();
        resourceLimits
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(null));

        database
            .Setup(d => d.GetCollection<BsonDocument>("ResourceLimits", It.IsAny<MongoCollectionSettings>()))
            .Returns(resourceLimits.Object);
        dbContext.Setup(d => d.GetDatabase("tenant-deny")).Returns(database.Object);

        var permissionsCollection = new Mock<IMongoCollection<BsonDocument>>();
        permissionsCollection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        dbContext.Setup(d => d.GetCollection<BsonDocument>("Permissions")).Returns(permissionsCollection.Object);

        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(s => s.ServiceName).Returns("svc");

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-deny")).Returns(CreateNonRootTenant("tenant-deny"));

        var handler = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        var requirement = (IAuthorizationRequirement)Activator.CreateInstance(requirementType!)!;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(BlocksContext.USER_ID_CLAIM, "user-1"),
            new Claim(BlocksContext.PERMISSION_CLAIM, "some-other-resource")
        ], "Bearer"));

        var http = new DefaultHttpContext();
        http.Request.Headers[BlocksConstants.BlocksKey] = "tenant-deny";
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor { ActionName = "Get", ControllerName = "Orders" }),
            "orders-get"));

        var context = new AuthorizationHandlerContext([requirement], principal, http);

        var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(handler, [context, requirement])!;
        await task;

        Assert.True(context.HasFailed);
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task IsProjectOwnerOrSharedAsync_ShouldReturnTrue_WhenProjectIsShared()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var dbContext = new Mock<IDbContextProvider>();
        var projectPeople = new Mock<IMongoCollection<BsonDocument>>();
        projectPeople
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(new BsonDocument { ["TenantId"] = "project-shared", ["UserId"] = "user-1" }));
        dbContext.Setup(d => d.GetCollection<BsonDocument>("ProjectPeoples")).Returns(projectPeople.Object);

        var blocksSecret = new Mock<IBlocksSecret>();
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("project-shared")).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = "project-shared",
            CreatedBy = "someone-else",
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = [],
                PublicCertificatePath = "path",
                PublicCertificatePassword = "pw",
                PrivateCertificatePassword = "private",
                IssueDate = DateTime.UtcNow
            }
        });

        var handler = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        var method = type!.GetMethod("IsProjectOwnerOrSharedAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<bool>)method!.Invoke(handler, ["user-1", "project-shared"])!;
        var result = await task;

        Assert.True(result);
    }

    [Fact]
    public async Task IsProjectOwnerOrSharedAsync_ShouldReturnFalse_WhenNotOwnerAndNotShared()
    {
        var type = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessHandler, Blocks.Genesis");
        Assert.NotNull(type);

        var dbContext = new Mock<IDbContextProvider>();
        var projectPeople = new Mock<IMongoCollection<BsonDocument>>();
        projectPeople
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(null));
        dbContext.Setup(d => d.GetCollection<BsonDocument>("ProjectPeoples")).Returns(projectPeople.Object);

        var blocksSecret = new Mock<IBlocksSecret>();
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("project-no-share")).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = "project-no-share",
            CreatedBy = "someone-else",
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = [],
                PublicCertificatePath = "path",
                PublicCertificatePassword = "pw",
                PrivateCertificatePassword = "private",
                IssueDate = DateTime.UtcNow
            }
        });

        var handler = Activator.CreateInstance(type!, dbContext.Object, blocksSecret.Object, tenants.Object);
        var method = type!.GetMethod("IsProjectOwnerOrSharedAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<bool>)method!.Invoke(handler, ["user-1", "project-no-share"])!;
        var result = await task;

        Assert.False(result);
    }

    private static Blocks.Genesis.Tenant CreateRootTenant(string tenantId)
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            IsRootTenant = true,
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
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
        };
    }

    private static Blocks.Genesis.Tenant CreateNonRootTenant(string tenantId)
    {
        var tenant = CreateRootTenant(tenantId);
        tenant.IsRootTenant = false;
        return tenant;
    }

    private static IAsyncCursor<BsonDocument> CreateCursor(BsonDocument? firstItem)
    {
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        if (firstItem is null)
        {
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>()))
                .Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns(Array.Empty<BsonDocument>());
        }
        else
        {
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>()))
                .Returns(true)
                .Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(true)
                .ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns([firstItem]);
        }

        return cursor.Object;
    }
}
