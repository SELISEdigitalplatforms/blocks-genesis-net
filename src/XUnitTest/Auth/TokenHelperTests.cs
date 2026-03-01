using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Security.Claims;

namespace XUnitTest.Auth;

public class TokenHelperTests : IDisposable
{
    public TokenHelperTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(CreateContext("tenant-1", "user-1"));
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
    }

    [Fact]
    public void GetToken_ShouldReadBearerTokenFromAuthorizationHeader()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer abc.def.ghi";
        var tenants = new Mock<ITenants>();

        var result = TokenHelper.GetToken(httpContext.Request, tenants.Object);

        Assert.Equal("abc.def.ghi", result.Token);
        Assert.False(result.IsThirdPartyToken);
    }

    [Fact]
    public void GetTokenFromCookie_ShouldReturnBlocksCookie_WhenAccessCookieExists()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = "access_token_tenant-1=blocks-token";
        var tenants = new Mock<ITenants>();

        var result = TokenHelper.GetTokenFromCookie(httpContext.Request, tenants.Object);

        Assert.Equal("blocks-token", result.Token);
        Assert.False(result.IsThirdPartyToken);
    }

    [Fact]
    public void GetTokenFromCookie_ShouldReturnThirdPartyCookie_WhenConfigured()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = "third_party_cookie=third-party-token";

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-1")).Returns(new Blocks.Genesis.Tenant
        {
            ApplicationDomain = "app",
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
            },
            ThirdPartyJwtTokenParameters = new ThirdPartyJwtTokenParameters
            {
                CookieKey = "third_party_cookie"
            }
        });

        var result = TokenHelper.GetTokenFromCookie(httpContext.Request, tenants.Object);

        Assert.Equal("third-party-token", result.Token);
        Assert.True(result.IsThirdPartyToken);
    }

    [Fact]
    public void HandleTokenIssuer_ShouldAppendRequestUriClaim()
    {
        var identity = new ClaimsIdentity();

        TokenHelper.HandleTokenIssuer(identity, "/v1/orders");

        var claim = identity.FindFirst(BlocksContext.REQUEST_URI_CLAIM);
        Assert.NotNull(claim);
        Assert.Equal("/v1/orders", claim!.Value);
    }

    private static BlocksContext CreateContext(string tenantId, string userId)
    {
        return BlocksContext.Create(
            tenantId,
            ["user"],
            userId,
            true,
            "/api/test",
            "org",
            DateTime.UtcNow.AddHours(1),
            "u@example.com",
            ["read"],
            "user",
            "123",
            "User",
            "token",
            tenantId);
    }
}