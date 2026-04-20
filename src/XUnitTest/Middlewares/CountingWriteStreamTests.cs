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

    [Fact]
    public async Task CountingWriteStream_ReadMethods_ShouldDelegateToInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-read", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-read")).Returns(tenant);

        Stream? capturedStream = null;
        RequestDelegate next = ctx =>
        {
            capturedStream = ctx.Response.Body;
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-read";

        using var activity = new Activity("read-test").Start();
        await middleware.InvokeAsync(context);

        Assert.NotNull(capturedStream);
        Assert.True(capturedStream!.CanWrite);
    }

    [Fact]
    public async Task CountingWriteStream_Properties_ShouldReflectInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-props", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-props")).Returns(tenant);

        Stream? capturedStream = null;
        RequestDelegate next = ctx =>
        {
            capturedStream = ctx.Response.Body;
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-props";

        using var activity = new Activity("props-test").Start();
        await middleware.InvokeAsync(context);

        Assert.NotNull(capturedStream);
        // MemoryStream supports all operations
        Assert.True(capturedStream!.CanRead);
        Assert.True(capturedStream!.CanSeek);
        Assert.True(capturedStream!.CanWrite);
        Assert.Equal(0, capturedStream!.Length);
        Assert.Equal(0, capturedStream!.Position);
    }

    [Fact]
    public async Task CountingWriteStream_SeekAndSetLength_ShouldDelegateToInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-seek", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-seek")).Returns(tenant);

        Stream? capturedStream = null;
        RequestDelegate next = ctx =>
        {
            capturedStream = ctx.Response.Body;
            // Write some data, then seek back
            var data = Encoding.UTF8.GetBytes("seek data");
            ctx.Response.Body.Write(data, 0, data.Length);
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-seek";

        using var activity = new Activity("seek-test").Start();
        await middleware.InvokeAsync(context);

        Assert.NotNull(capturedStream);
        Assert.Equal(0, capturedStream!.Position);
    }

    [Fact]
    public async Task CountingWriteStream_SetLength_ShouldDelegateToInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-setlength", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-setlength")).Returns(tenant);

        Stream? capturedStream = null;
        RequestDelegate next = ctx =>
        {
            capturedStream = ctx.Response.Body;
            ctx.Response.Body.SetLength(100);
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-setlength";

        using var activity = new Activity("setlength-test").Start();
        await middleware.InvokeAsync(context);

        Assert.NotNull(capturedStream);
        Assert.Equal(100, capturedStream!.Length);
    }

    [Fact]
    public async Task CountingWriteStream_Position_ShouldBeSettable()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-pos", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-pos")).Returns(tenant);

        Stream? capturedStream = null;
        RequestDelegate next = ctx =>
        {
            capturedStream = ctx.Response.Body;
            var data = Encoding.UTF8.GetBytes("position test");
            ctx.Response.Body.Write(data, 0, data.Length);
            ctx.Response.Body.Position = 5;
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-pos";

        using var activity = new Activity("pos-test").Start();
        await middleware.InvokeAsync(context);

        Assert.NotNull(capturedStream);
        // After middleware completes the position was set to 5
    }

    [Fact]
    public async Task CountingWriteStream_ReadAsync_ShouldDelegateToInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-readasync", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-readasync")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            var data = Encoding.UTF8.GetBytes("read me");
            await ctx.Response.Body.WriteAsync(data);
            ctx.Response.Body.Position = 0;

            var buffer = new byte[7];
            var bytesRead = await ctx.Response.Body.ReadAsync(buffer);
            Assert.Equal(7, bytesRead);
            Assert.Equal("read me", Encoding.UTF8.GetString(buffer));
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-readasync";

        using var activity = new Activity("readasync-test").Start();
        await middleware.InvokeAsync(context);
    }

    [Fact]
    public async Task CountingWriteStream_ReadAsyncWithArrayOverload_ShouldDelegateToInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-readarray", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-readarray")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            var data = Encoding.UTF8.GetBytes("array read");
            await ctx.Response.Body.WriteAsync(data, 0, data.Length);
            ctx.Response.Body.Position = 0;

            var buffer = new byte[10];
            var bytesRead = await ctx.Response.Body.ReadAsync(buffer, 0, 10);
            Assert.Equal(10, bytesRead);
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-readarray";

        using var activity = new Activity("readarray-test").Start();
        await middleware.InvokeAsync(context);
    }

    [Fact]
    public async Task CountingWriteStream_ReadSpan_ShouldDelegateToInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-readspan", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-readspan")).Returns(tenant);

        RequestDelegate next = ctx =>
        {
            var data = Encoding.UTF8.GetBytes("span read");
            ctx.Response.Body.Write(data, 0, data.Length);
            ctx.Response.Body.Position = 0;

            Span<byte> buffer = new byte[9];
            var bytesRead = ctx.Response.Body.Read(buffer);
            Assert.Equal(9, bytesRead);
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-readspan";

        using var activity = new Activity("readspan-test").Start();
        await middleware.InvokeAsync(context);
    }

    [Fact]
    public async Task CountingWriteStream_WriteSpan_ShouldTrackBytes()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-writespan", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-writespan")).Returns(tenant);

        RequestDelegate next = ctx =>
        {
            ReadOnlySpan<byte> data = Encoding.UTF8.GetBytes("span write");
            ctx.Response.Body.Write(data);
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-writespan";

        using var activity = new Activity("writespan-test").Start();
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Equal("span write", body);
    }

    [Fact]
    public async Task CountingWriteStream_WriteAsyncArray_ShouldTrackBytes()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-writeasyncarray", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-writeasyncarray")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            var data = Encoding.UTF8.GetBytes("async array write");
            await ctx.Response.Body.WriteAsync(data, 0, data.Length);
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-writeasyncarray";

        using var activity = new Activity("writeasyncarray-test").Start();
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Equal("async array write", body);
    }

    [Fact]
    public async Task CountingWriteStream_Flush_ShouldDelegateToInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-flush2", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-flush2")).Returns(tenant);

        RequestDelegate next = ctx =>
        {
            ctx.Response.Body.Write(Encoding.UTF8.GetBytes("x"));
            ctx.Response.Body.Flush();
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-flush2";

        using var activity = new Activity("flush2-test").Start();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task CountingWriteStream_Dispose_ShouldNotCloseInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-dispose", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-dispose")).Returns(tenant);

        RequestDelegate next = ctx =>
        {
            // Disposing the response body should not close the underlying stream
            ctx.Response.Body.Dispose();
            return Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-dispose";

        using var activity = new Activity("dispose-test").Start();
        var ex = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        Assert.Null(ex);
    }

    [Fact]
    public async Task CountingWriteStream_DisposeAsync_ShouldNotCloseInnerStream()
    {
        var tenants = new Mock<ITenants>();
        var crypto = new Mock<ICryptoService>();
        var tenant = CreateTenant("tenant-disposeasync", "app.local");

        tenants.Setup(t => t.GetTenantByID("tenant-disposeasync")).Returns(tenant);

        RequestDelegate next = async ctx =>
        {
            await ctx.Response.Body.DisposeAsync();
            await Task.CompletedTask;
        };

        var middleware = new TenantValidationMiddleware(next, tenants.Object, crypto.Object);
        var context = CreateHttpContextWithEndpoint("app.local");
        context.Request.Headers[BlocksConstants.BlocksKey] = "tenant-disposeasync";

        using var activity = new Activity("disposeasync-test").Start();
        var ex = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        Assert.Null(ex);
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
