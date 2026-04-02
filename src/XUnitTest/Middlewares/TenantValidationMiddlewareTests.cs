using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Diagnostics;
using System.Text;

namespace XUnitTest.Middlewares;

public class TenantValidationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldReturn404_WhenTenantNotFoundByDomain()
    {
        var tenants = new Mock<ITenantLookup>();
        var crypto = new Mock<ICryptoService>();
        tenants.Setup(t => t.GetTenantByApplicationDomain("unknown.local")).Returns((Blocks.Genesis.Tenant?)null);

        var middleware = new TenantValidationMiddleware(_ => Task.CompletedTask, tenants.Object, crypto.Object);
        var context = CreateHttpContext("unknown.local");

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn406_WhenOriginIsInvalid()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-1", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-1")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(_ => Task.CompletedTask, tenants.Object, crypto.Object);
        var context = CreateHttpContext("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-1";
        context.Request.Headers.Origin = "::::invalid-origin::::";
        context.Request.Headers.Referer = "::::invalid-referer::::";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status406NotAcceptable, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn403_WhenGrpcKeyDoesNotMatch()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-1", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-1")).Returns(tenant);
        crypto.Setup(c => c.Hash("tenant-1", tenant.TenantSalt)).Returns("expected-hash");

        var middleware = new TenantValidationMiddleware(_ => Task.CompletedTask, tenants.Object, crypto.Object);
        var context = CreateHttpContext("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-1";
        context.Request.Headers[BlocksConstants.BlocksGrpcKey] = "wrong-hash";
        context.Request.ContentType = "application/grpc";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_AndClearContext_OnSuccess()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-1", "app.local");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-1")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsync("ok");
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContext("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-1";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);
    Assert.True(nextCalled);
    Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    var contextVal = BlocksContext.GetContext();
    Assert.NotNull(contextVal);
    Assert.Equal(string.Empty, contextVal.TenantId);
    Assert.Empty(contextVal.Roles);
    Assert.Equal(string.Empty, contextVal.UserId);
    Assert.False(contextVal.IsAuthenticated);
    }

    [Fact]
    public async Task InvokeAsync_ShouldPreserveOriginalResponseStream_ForStreamingWrites()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-1", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-1")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            await ctx.Response.WriteAsync("chunk-1");
            await ctx.Response.Body.FlushAsync();
            await ctx.Response.WriteAsync("chunk-2");
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContext("app.local");
        var originalStream = context.Response.Body;
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-1";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.Same(originalStream, context.Response.Body);

        originalStream.Position = 0;
        using var reader = new StreamReader(originalStream, Encoding.UTF8, leaveOpen: true);
        var responseBody = await reader.ReadToEndAsync();

        Assert.Equal("chunk-1chunk-2", responseBody);
    }

    private static DefaultHttpContext CreateHttpContext(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "TestController");
        context.SetEndpoint(endpoint);
        return context;
    }

    private static Blocks.Genesis.Tenant CreateTenant(string tenantId, string applicationDomain)
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            ApplicationDomain = applicationDomain,
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
}