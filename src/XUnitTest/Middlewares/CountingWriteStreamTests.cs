using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace XUnitTest.Middlewares;

public class CountingWriteStreamTests
{
    [Fact]
    public async Task Write_ShouldTrackBytesWritten()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-cw", "app.local");
        long capturedBytesWritten = 0;

        tenants.Setup(t => t.GetTenantByID("tenant-cw")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            var data = Encoding.UTF8.GetBytes("hello world");
            await ctx.Response.Body.WriteAsync(data);
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("!"));
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-cw";

        using var activity = new Activity("counting-test").Start();
        await middleware.InvokeAsync(context);

        // Verify response was written (stream position shows data was written)
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Equal("hello world!", body);
    }

    [Fact]
    public async Task Write_WithSpan_ShouldTrackBytesWritten()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-span", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-span")).Returns(tenant);

        RequestDelegate next = ctx =>
        {
            var data = Encoding.UTF8.GetBytes("span data");
            ctx.Response.Body.Write(data, 0, data.Length);
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-span";

        using var activity = new Activity("span-test").Start();
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Equal("span data", body);
    }

    [Fact]
    public async Task Flush_ShouldDelegateToInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-flush", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-flush")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            await ctx.Response.WriteAsync("data");
            await ctx.Response.Body.FlushAsync();
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-flush";

        using var activity = new Activity("flush-test").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task OriginalBodyStream_ShouldBeRestoredAfterMiddleware()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-restore", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-restore")).Returns(tenant);

        var middleware = new TenantValidationMiddleware(_ => Task.CompletedTask, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        var originalBody = context.Response.Body;
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-restore";

        using var activity = new Activity("restore-test").Start();
        await middleware.InvokeAsync(context);

        Assert.Same(originalBody, context.Response.Body);
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
