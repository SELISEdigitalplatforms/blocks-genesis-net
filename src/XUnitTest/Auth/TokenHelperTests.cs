using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Security.Claims;

namespace XUnitTest.Auth;

public class TokenHelperTests : IDisposable
{
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
    public void HandleTokenIssuer_ShouldAppendRequestUriClaim()
    {
        var identity = new ClaimsIdentity();

        TokenHelper.HandleTokenIssuer(identity, "/v1/orders");

        var claim = identity.FindFirst(BlocksContext.REQUEST_URI_CLAIM);
        Assert.NotNull(claim);
        Assert.Equal("/v1/orders", claim!.Value);
    }
}