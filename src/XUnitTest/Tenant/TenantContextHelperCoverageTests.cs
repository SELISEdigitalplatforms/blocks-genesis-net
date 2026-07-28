using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text;

namespace XUnitTest.Tenant;

public class TenantContextHelperCoverageTests
{
    private static readonly Type HelperType = Type.GetType("Blocks.Genesis.TenantContextHelper, Blocks.Genesis")!;

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldReadTenantIdFromForm()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            [ResolutionKey()] = "tenant-from-form"
        });

        var tenantId = await ResolveTenantIdAsync(context.Request, null);

        Assert.Equal("tenant-from-form", tenantId);
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldFallThroughToToken_WhenFormHasNoTenantId()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["unrelated-field"] = "value"
        });

        var token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: [new Claim(BlocksContext.TENANT_ID_CLAIM, "tenant-after-empty-form")]));

        var tenantId = await ResolveTenantIdAsync(context.Request, token);

        Assert.Equal("tenant-after-empty-form", tenantId);
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldIgnoreUnparseableForm_AndFallBackToToken()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("not-a-form"));

        var token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: [new Claim(BlocksContext.TENANT_ID_CLAIM, "tenant-from-token")]));

        var tenantId = await ResolveTenantIdAsync(context.Request, token);

        Assert.Equal("tenant-from-token", tenantId);
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldReturnNull_WhenTokenHasNoTenantClaim()
    {
        var context = new DefaultHttpContext();

        var token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: [new Claim("sub", "someone")]));

        var tenantId = await ResolveTenantIdAsync(context.Request, token);

        Assert.Null(tenantId);
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldReturnNull_WhenTokenIsMalformed()
    {
        var context = new DefaultHttpContext();

        var tenantId = await ResolveTenantIdAsync(context.Request, "not-a-jwt");

        Assert.Null(tenantId);
    }

    [Fact]
    public void IsDomainAllowed_ShouldAllowMissingHeader()
    {
        Assert.True(IsDomainAllowed(null, MakeTenant()));
        Assert.True(IsDomainAllowed("  ", MakeTenant()));
    }

    [Fact]
    public void IsDomainAllowed_ShouldHandleNullApplications()
    {
        var tenant = MakeTenant();
        tenant.Applications = null!;

        Assert.False(IsDomainAllowed("https://app.local", tenant));
    }

    [Fact]
    public void IsDomainAllowed_ShouldMatchNormalizedDomains()
    {
        var tenant = MakeTenant("https://app.local");

        Assert.True(IsDomainAllowed("https://app.local", tenant));
        Assert.False(IsDomainAllowed("https://other.local", tenant));
        Assert.False(IsDomainAllowed("::invalid::", tenant));
    }

    [Fact]
    public void ResolveApplicationDomain_ShouldReturnMatchingDomain_ForOrigin()
    {
        var tenant = MakeTenant("https://app.local");

        Assert.Equal("https://app.local", ResolveApplicationDomain(tenant, "https://app.local", null));
    }

    [Fact]
    public void ResolveApplicationDomain_ShouldReturnMatchingDomain_ForReferer()
    {
        var tenant = MakeTenant("https://app.local");

        Assert.Equal("https://app.local", ResolveApplicationDomain(tenant, null, "https://app.local/page"));
    }

    [Fact]
    public void ResolveApplicationDomain_ShouldReturnEmpty_WhenNoMatchOrUnparseable()
    {
        var tenant = MakeTenant("https://app.local");

        Assert.Equal(string.Empty, ResolveApplicationDomain(tenant, "https://unknown.local", null));
        Assert.Equal(string.Empty, ResolveApplicationDomain(tenant, "::invalid::", null));
        Assert.Equal(string.Empty, ResolveApplicationDomain(tenant, "http://localhost:3000", null));
        Assert.Equal(string.Empty, ResolveApplicationDomain(tenant, null, null));
    }

    [Fact]
    public void ResolveApplicationDomain_ShouldReturnEmpty_WhenApplicationsIsNull()
    {
        var tenant = MakeTenant();
        tenant.Applications = null!;

        Assert.Equal(string.Empty, ResolveApplicationDomain(tenant, "https://app.local", null));
    }

    private static async Task<string?> ResolveTenantIdAsync(HttpRequest request, string? token)
    {
        var method = HelperType.GetMethod("ResolveTenantIdAsync", BindingFlags.Public | BindingFlags.Static, [typeof(HttpRequest), typeof(string)]);
        Assert.NotNull(method);
        return await (Task<string?>)method!.Invoke(null, [request, token])!;
    }

    private static bool IsDomainAllowed(string? headerValue, Blocks.Genesis.Tenant tenant)
    {
        var method = HelperType.GetMethod("IsDomainAllowed", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [headerValue, tenant])!;
    }

    private static string ResolveApplicationDomain(Blocks.Genesis.Tenant tenant, string? origin, string? referer)
    {
        var method = HelperType.GetMethod("ResolveApplicationDomain", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [tenant, origin, referer])!;
    }

    private static string ResolutionKey()
    {
        var field = HelperType.GetField("TenantResolutionKeys", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var keys = (string[])field!.GetValue(null)!;
        return keys[0];
    }

    private static Blocks.Genesis.Tenant MakeTenant(params string[] domains)
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = "tenant-helper",
            DbConnectionString = "mongodb://127.0.0.1:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer", Subject = "subject", Audiences = [],
                PublicCertificatePath = "path", PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty, IssueDate = DateTime.UtcNow
            },
            Applications = domains.Select(d => new Applications { Domain = d }).ToList()
        };
    }
}
