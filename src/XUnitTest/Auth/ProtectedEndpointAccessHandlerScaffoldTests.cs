using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
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
}
