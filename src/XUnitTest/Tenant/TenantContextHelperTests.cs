using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using System.Reflection;

namespace XUnitTest.Tenant;

/// <summary>
/// Tests for the internal <c>TenantContextHelper</c>. <c>ResolveTenantIdAsync</c> resolves the
/// tenant id from request headers, query string, form body, then a JWT token (in that order);
/// <c>EnsureTenantContext</c> seeds a <see cref="BlocksContext"/> for a resolved tenant unless the
/// tenant is null or the current context already targets the same tenant.
/// </summary>
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
    public async Task ResolveTenantId_ShouldReturnHeaderValue()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[BlocksConstants.BlocksKey] = "header-tenant";

        var result = await InvokeResolveAsync(httpContext.Request, null);

        Assert.Equal("header-tenant", result);
    }

    [Fact]
    public async Task ResolveTenantId_ShouldReturnQueryValue_WhenNoHeader()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString($"?{BlocksConstants.BlocksKey}=query-tenant");

        var result = await InvokeResolveAsync(httpContext.Request, null);

        Assert.Equal("query-tenant", result);
    }

    [Fact]
    public async Task ResolveTenantId_ShouldReturnNull_WhenNoHeaderOrQueryOrToken()
    {
        var httpContext = new DefaultHttpContext();

        var result = await InvokeResolveAsync(httpContext.Request, null);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveTenantId_ShouldReturnNull_ForInvalidToken()
    {
        var httpContext = new DefaultHttpContext();

        var result = await InvokeResolveAsync(httpContext.Request, "not-a-valid-jwt");

        Assert.Null(result);
    }

    [Fact]
    public void EnsureTenantContext_ShouldSetContext_WhenTenantIsProvided()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString("app.local");

        GetEnsureMethod().Invoke(null, [httpContext, CreateTenant("my-tenant")]);

        var ctx = BlocksContext.GetContext();
        Assert.NotNull(ctx);
        Assert.Equal("my-tenant", ctx!.TenantId);
    }

    [Fact]
    public void EnsureTenantContext_ShouldNotSetContext_WhenTenantIsNull()
    {
        var httpContext = new DefaultHttpContext();

        GetEnsureMethod().Invoke(null, [httpContext, null]);

        var ctx = BlocksContext.GetContext();
        Assert.True(ctx == null || string.IsNullOrEmpty(ctx.UserId));
    }

    [Fact]
    public void EnsureTenantContext_ShouldSkip_WhenAlreadySameTenant()
    {
        var existing = BlocksContext.Create(
            "same-tenant", ["admin"], "u1", true, "/api", "org",
            DateTime.MinValue, "e@e.com", ["read"], "user", "", "", "token", "same-tenant");
        BlocksContext.SetContext(existing);

        var httpContext = new DefaultHttpContext();

        GetEnsureMethod().Invoke(null, [httpContext, CreateTenant("same-tenant")]);

        var ctx = BlocksContext.GetContext();
        Assert.Equal("same-tenant", ctx!.TenantId);
        Assert.Equal("u1", ctx.UserId); // Original context preserved because the tenant is unchanged.
    }

    // ---- helpers ----

    private static async Task<string?> InvokeResolveAsync(HttpRequest request, string? token)
    {
        var type = typeof(BlocksContext).Assembly.GetType("Blocks.Genesis.TenantContextHelper")!;
        var method = type.GetMethod("ResolveTenantIdAsync", BindingFlags.Public | BindingFlags.Static)!;
        return await (Task<string?>)method.Invoke(null, [request, token])!;
    }

    private static MethodInfo GetEnsureMethod()
    {
        var type = typeof(BlocksContext).Assembly.GetType("Blocks.Genesis.TenantContextHelper")!;
        return type.GetMethod("EnsureTenantContext", BindingFlags.Public | BindingFlags.Static)!;
    }

    private static Blocks.Genesis.Tenant CreateTenant(string tenantId) => new()
    {
        TenantId = tenantId,
        Applications = [new Blocks.Genesis.Applications { Domain = "app.local" }],
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
    };
}
