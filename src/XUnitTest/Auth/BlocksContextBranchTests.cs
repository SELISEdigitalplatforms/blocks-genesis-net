using Blocks.Genesis;
using Microsoft.AspNetCore.Http;

namespace XUnitTest.Auth;

/// <summary>Branch coverage for <see cref="BlocksContext"/> domain resolution/normalisation and cleanup.</summary>
public class BlocksContextBranchTests
{
    [Fact]
    public void ResolveApplicationDomain_ShouldPreferOrigin_ThenReferer_ElseEmpty()
    {
        Assert.Equal(string.Empty, BlocksContext.ResolveApplicationDomain(null));

        var origin = new DefaultHttpContext();
        origin.Request.Headers.Origin = "https://app.local/dashboard";
        Assert.Equal("app.local", BlocksContext.ResolveApplicationDomain(origin.Request));

        var referer = new DefaultHttpContext();
        referer.Request.Headers.Referer = "http://ref.local:5001/x";
        Assert.Equal("ref.local", BlocksContext.ResolveApplicationDomain(referer.Request));

        Assert.Equal(string.Empty, BlocksContext.ResolveApplicationDomain(new DefaultHttpContext().Request));
    }

    [Theory]
    [InlineData("https://app.local/path", "app.local")]
    [InlineData("http://app.local:5001", "app.local")]
    [InlineData("app.local", "app.local")]
    [InlineData("", "")]
    public void NormalizeDomain_ShouldStripSchemePortAndPath(string url, string expected)
    {
        Assert.Equal(expected, BlocksContext.NormalizeDomain(url));
    }
}
