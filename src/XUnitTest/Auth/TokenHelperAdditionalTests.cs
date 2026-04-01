using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Security.Claims;

namespace XUnitTest.Auth;

public class TokenHelperAdditionalTests : IDisposable
{
    public TokenHelperAdditionalTests()
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
    public void GetToken_ShouldFallbackToCookie_WhenNoAuthorizationHeader()
    {
        var httpContext = new DefaultHttpContext();
        // No Authorization header; BlocksContext has empty TenantId, so GetTokenFromCookie returns empty
        var tenants = new Mock<ITenants>();

        var result = TokenHelper.GetToken(httpContext.Request, tenants.Object);

        Assert.Equal(string.Empty, result.Token);
        Assert.False(result.IsThirdPartyToken);
    }

    [Fact]
    public void GetToken_ShouldIgnoreNonBearerAuthorizationHeader()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Basic dXNlcjpwYXNz";
        var tenants = new Mock<ITenants>();

        var result = TokenHelper.GetToken(httpContext.Request, tenants.Object);

        Assert.Equal(string.Empty, result.Token);
        Assert.False(result.IsThirdPartyToken);
    }

    [Fact]
    public void GetTokenFromCookie_ShouldReturnEmpty_WhenTenantIdIsNull()
    {
        var httpContext = new DefaultHttpContext();
        var tenants = new Mock<ITenants>();

        // BlocksContext returns default with empty TenantId
        var result = TokenHelper.GetTokenFromCookie(httpContext.Request, tenants.Object);

        Assert.Equal(string.Empty, result.Token);
        Assert.False(result.IsThirdPartyToken);
    }

    [Fact]
    public void GetTokenFromCookie_ShouldReturnAccessToken_WhenCookieExists()
    {
        var ctx = BlocksContext.Create(
            "tenant-cookie", [], "", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");
        BlocksContext.SetContext(ctx);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Append("Cookie", "access_token_tenant-cookie=mytoken123");
        var tenants = new Mock<ITenants>();

        var result = TokenHelper.GetTokenFromCookie(httpContext.Request, tenants.Object);

        Assert.Equal("mytoken123", result.Token);
        Assert.False(result.IsThirdPartyToken);
    }

    [Fact]
    public void GetTokenFromCookie_ShouldReturnEmpty_WhenNoCookieKeyConfigured()
    {
        var ctx = BlocksContext.Create(
            "tenant-nocookie", [], "", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");
        BlocksContext.SetContext(ctx);

        var httpContext = new DefaultHttpContext();
        var tenants = new Mock<ITenants>();
        var tenant = new Blocks.Genesis.Tenant
        {
            TenantId = "tenant-nocookie",
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters { Issuer = "i", Subject = "s", Audiences = [], PublicCertificatePath = "p", PublicCertificatePassword = "pw", PrivateCertificatePassword = "pw", IssueDate = DateTime.UtcNow },
            ThirdPartyJwtTokenParameters = null
        };
        tenants.Setup(t => t.GetTenantByID("tenant-nocookie")).Returns(tenant);

        var result = TokenHelper.GetTokenFromCookie(httpContext.Request, tenants.Object);

        Assert.Equal(string.Empty, result.Token);
        Assert.False(result.IsThirdPartyToken);
    }

    [Fact]
    public void GetTokenFromCookie_ShouldReturnThirdPartyToken_WhenCookieKeyIsConfigured()
    {
        var ctx = BlocksContext.Create(
            "tenant-tp", [], "", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");
        BlocksContext.SetContext(ctx);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Append("Cookie", "third_party_token=tp-token-value");
        var tenants = new Mock<ITenants>();
        var tenant = new Blocks.Genesis.Tenant
        {
            TenantId = "tenant-tp",
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters { Issuer = "i", Subject = "s", Audiences = [], PublicCertificatePath = "p", PublicCertificatePassword = "pw", PrivateCertificatePassword = "pw", IssueDate = DateTime.UtcNow },
            ThirdPartyJwtTokenParameters = new ThirdPartyJwtTokenParameters { CookieKey = "third_party_token" }
        };
        tenants.Setup(t => t.GetTenantByID("tenant-tp")).Returns(tenant);

        var result = TokenHelper.GetTokenFromCookie(httpContext.Request, tenants.Object);

        Assert.Equal("tp-token-value", result.Token);
        Assert.True(result.IsThirdPartyToken);
    }

    [Fact]
    public void HandleTokenIssuer_ShouldThrowOnNullClaimsIdentity()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TokenHelper.HandleTokenIssuer(null!, "/api"));
    }
}
