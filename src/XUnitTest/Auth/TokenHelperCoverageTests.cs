using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;

namespace XUnitTest.Auth;

[Collection("BlocksAuthStaticState")]
public class TokenHelperCoverageTests : IDisposable
{
    public TokenHelperCoverageTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
    }

    [Fact]
    public void GetTokenFromCookie_ShouldReturnEmpty_WhenThirdPartyCookieIsAbsent()
    {
        BlocksContext.SetContext(BlocksContext.Create(
            "tenant-tp", [], "", false, "", "", DateTime.MinValue, "", [], "", "", "", "", "tenant-tp"));

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-tp")).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = "tenant-tp",
            Applications = [new Blocks.Genesis.Applications { Domain = "app.local" }],
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = [],
                PublicCertificatePath = "path",
                PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty,
                IssueDate = DateTime.UtcNow
            },
            ThirdPartyJwtTokenParameters = new ThirdPartyJwtTokenParameters
            {
                CookieKey = "tp_cookie"
            }
        });

        // The cookie key is configured but the request carries no such cookie.
        var http = new DefaultHttpContext();
        http.Request.Headers.Origin = "https://app.local";

        var (token, isThirdParty) = TokenHelper.GetTokenFromCookie(http.Request, tenants.Object);

        Assert.Equal(string.Empty, token);
        Assert.False(isThirdParty);
    }
}
