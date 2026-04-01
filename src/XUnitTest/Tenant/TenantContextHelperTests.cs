using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using System.Reflection;

namespace XUnitTest.Tenant;

public class TenantContextHelperTests : IDisposable
{
    public TenantContextHelperTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
        BlocksHttpContextAccessor.Instance = null;
    }

    [Fact]
    public void ResolveTenantId_ShouldReturnHeaderValue()
    {
        var method = GetResolveMethod();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[BlocksConstants.BlocksKey] = "header-tenant";

        var result = (string?)method.Invoke(null, [httpContext.Request, null]);

        Assert.Equal("header-tenant", result);
    }

    [Fact]
    public void ResolveTenantId_ShouldReturnQueryValue_WhenNoHeader()
    {
        var method = GetResolveMethod();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString($"?{BlocksConstants.BlocksKey}=query-tenant");

        var result = (string?)method.Invoke(null, [httpContext.Request, null]);

        Assert.Equal("query-tenant", result);
    }

    [Fact]
    public void ResolveTenantId_ShouldReturnNull_WhenNoHeaderOrQueryOrToken()
    {
        var method = GetResolveMethod();
        var httpContext = new DefaultHttpContext();

        var result = (string?)method.Invoke(null, [httpContext.Request, null]);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveTenantId_ShouldReturnNull_ForInvalidToken()
    {
        var method = GetResolveMethod();
        var httpContext = new DefaultHttpContext();

        var result = (string?)method.Invoke(null, [httpContext.Request, "not-a-valid-jwt"]);

        Assert.Null(result);
    }

    [Fact]
    public void EnsureTenantContext_ShouldSetContext_WhenTenantIdNotEmpty()
    {
        var method = GetEnsureMethod();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString("app.local");

        method.Invoke(null, [httpContext, "my-tenant"]);

        var ctx = BlocksContext.GetContext();
        Assert.NotNull(ctx);
        Assert.Equal("my-tenant", ctx!.TenantId);
    }

    [Fact]
    public void EnsureTenantContext_ShouldNotSetContext_WhenTenantIdIsEmpty()
    {
        var method = GetEnsureMethod();
        var httpContext = new DefaultHttpContext();

        method.Invoke(null, [httpContext, ""]);

        var ctx = BlocksContext.GetContext();
        Assert.True(ctx == null || string.IsNullOrEmpty(ctx.UserId));
    }

    [Fact]
    public void EnsureTenantContext_ShouldSkip_WhenAlreadySameTenant()
    {
        var existing = BlocksContext.Create(
            "same-tenant", ["admin"], "u1", true, "/api", "org",
            DateTime.MinValue, "e@e.com", ["read"], "user", "", "", "token", "", "same-tenant");
        BlocksContext.SetContext(existing);

        var method = GetEnsureMethod();
        var httpContext = new DefaultHttpContext();

        method.Invoke(null, [httpContext, "same-tenant"]);

        var ctx = BlocksContext.GetContext();
        Assert.Equal("same-tenant", ctx!.TenantId);
        Assert.Equal("u1", ctx.UserId); // Original context preserved
    }

    private static MethodInfo GetResolveMethod()
    {
        var type = typeof(BlocksContext).Assembly.GetType("Blocks.Genesis.TenantContextHelper")!;
        return type.GetMethod("ResolveTenantId", BindingFlags.Public | BindingFlags.Static)!;
    }

    private static MethodInfo GetEnsureMethod()
    {
        var type = typeof(BlocksContext).Assembly.GetType("Blocks.Genesis.TenantContextHelper")!;
        return type.GetMethod("EnsureTenantContext", BindingFlags.Public | BindingFlags.Static)!;
    }
}
