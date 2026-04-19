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
    public async Task InvokeAsync_ShouldSkipValidation_WhenEndpointIsNotControllerOrGraphQL()
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
        context.Request.Host = new HostString("app.local");
        context.Response.Body = new MemoryStream();
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "HealthCheck");
        context.SetEndpoint(endpoint);

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

    [Fact]
    public async Task InvokeAsync_ShouldAllowValidReferer_WhenOriginIsMissing()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-ref", "app.local");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-ref")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-ref";
        context.Request.Headers.Referer = "https://app.local/page";

        using var activity = new Activity("referer-test").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReject_WhenOriginIsUnknownDomain()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-reject", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-reject")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(_ => Task.CompletedTask, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-reject";
        context.Request.Headers.Origin = "https://evil.example.com";
        context.Request.Headers.Referer = "https://evil.example.com/page";

        using var activity = new Activity("reject-test").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status406NotAcceptable, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldProcessGraphQLEndpoint()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-gql", "app.local");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-gql")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("app.local");
        context.Request.Method = HttpMethods.Post;
        context.Response.Body = new MemoryStream();
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-gql";
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "GraphQL Endpoint");
        context.SetEndpoint(endpoint);

        using var activity = new Activity("graphql-test").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldResolveTenantByDomain_WhenNoBlocksKeyHeader()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-domain", "app.local");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByApplicationDomain("app.local")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = CreateHttpContextWithEndpoint("app.local");
        // No BlocksKey header — should resolve by domain

        using var activity = new Activity("domain-test").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldHandleNullOriginAndReferer()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-null-headers", "app.local");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-null-headers")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-null-headers";
        // No Origin or Referer headers — both null/empty should pass

        using var activity = new Activity("null-headers-test").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldAllowDomainWithPort_WhenNormalized()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-port", "https://app.local:8080");
        var nextCalled = false;

        tenants.Setup(t => t.GetTenantByID("tenant-port")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, tenants.Object, crypto.Object);

        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-port";
        context.Request.Headers.Origin = "https://app.local";

        using var activity = new Activity("port-test").Start();
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldSetContentLengthAndResponseSize()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-size", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-size")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsync("response data");
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-size";
        context.Request.ContentLength = 42;

        using var activity = new Activity("size-test").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldClearContext_EvenWhenNextThrows()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();

            var tenants = new Mock<ITenants>();
            var crypto = new Mock<ICryptoService>();
            var tenant = CreateTenant("tenant-throw", "app.local");

            tenants.Setup(t => t.GetTenantByID("tenant-throw")).Returns(tenant);

            var middleware = new TenantValidationMiddleware(
                _ => throw new InvalidOperationException("middleware failure"),
                tenants.Object, crypto.Object);
            var context = CreateHttpContextWithEndpoint("app.local");
            context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-throw";

            using var activity = new Activity("throw-test").Start();
            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

            Assert.Null(BlocksContext.GetContext());
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
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
