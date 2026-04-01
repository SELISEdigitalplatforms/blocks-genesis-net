using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Diagnostics;
using System.Text;

namespace XUnitTest.Middlewares;

public class TenantValidationMiddlewareAdditionalTests
{
    [Fact]
    public async Task InvokeAsync_ShouldSkipValidation_WhenNoEndpoint()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var nextCalled = false;

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = new DefaultHttpContext();
        // No endpoint set - should skip tenant validation
        context.Request.Host = new HostString("app.local");
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn404_WhenTenantIsDisabled()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-disabled", "app.local");
        tenant.IsDisabled = true;

        tenants.Setup(t => t.GetTenantByID("tenant-disabled")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(_ => Task.CompletedTask, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-disabled";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn404_WhenTenantIdProvidedButTenantMissing()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();

        tenants.Setup(t => t.GetTenantByID("nonexistent")).Returns((Blocks.Genesis.Tenant?)null);

        var middleware = new TenantValidationMiddleware(_ => Task.CompletedTask, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "nonexistent";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldPassGrpc_WhenHashMatches()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-grpc", "app.local");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-grpc")).Returns(tenant);
        crypto.Setup(c => c.Hash("tenant-grpc", tenant.TenantSalt)).Returns("correct-hash");

        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-grpc";
        context.Request.Headers[BlocksConstants.BlocksGrpcKey] = "correct-hash";
        context.Request.ContentType = "application/grpc";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldAllowValidOrigin()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-origin", "app.local");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-origin")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-origin";
        context.Request.Headers.Origin = "http://app.local";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldAllowLocalhostOrigin()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-lh", "app.local");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-lh")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-lh";
        context.Request.Headers.Origin = "http://localhost:3000";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldAllowAllowedDomain()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-ad", "app.local");
        tenant.AllowedDomains = ["extra.local"];
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-ad")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-ad";
        context.Request.Headers.Origin = "https://extra.local";

        using var activity = new Activity("tenant-validation").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenNextIsNull()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();

        Assert.Throws<ArgumentNullException>(() =>
            new TenantValidationMiddleware(null!, tenants.Object, crypto.Object));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenTenantsIsNull()
    {
        var crypto = new Mock<ICryptoService>();

        Assert.Throws<ArgumentNullException>(() =>
            new TenantValidationMiddleware(_ => Task.CompletedTask, null!, crypto.Object));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenCryptoIsNull()
    {
        var tenants = new Mock<ITenants>();

        Assert.Throws<ArgumentNullException>(() =>
            new TenantValidationMiddleware(_ => Task.CompletedTask, tenants.Object, null!));
    }

    private static DefaultHttpContext CreateHttpContextWithEndpoint(string host)
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
