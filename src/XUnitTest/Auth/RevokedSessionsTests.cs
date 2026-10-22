using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;

namespace XUnitTest.Auth;

/// <summary>
/// The revoked-session mark IAM writes on "Sign out of this device", and the token check that refuses
/// an access token whose <c>sid</c> names a marked session.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class RevokedSessionsTests
{
    private const string ExtensionTypeName = "Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis";
    private const string SessionId = "5301086ffb2147909952eb4b1d50e8ef";

    // ---------- RevokedSessions ----------

    [Fact]
    public void KeyFor_PrefixesTheSessionId()
    {
        Assert.Equal("revoked_session:" + SessionId, RevokedSessions.KeyFor(SessionId));
    }

    [Fact]
    public async Task MarkAsync_WritesTheKeyForTheWholeLifetimeInSeconds()
    {
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.AddStringValueAsync(RevokedSessions.KeyFor(SessionId), "1", 901)).ReturnsAsync(true);

        var marked = await RevokedSessions.MarkAsync(cache.Object, SessionId, TimeSpan.FromSeconds(900.2));

        Assert.True(marked);
        cache.VerifyAll();
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("", 60)]
    [InlineData("  ", 60)]
    [InlineData(SessionId, 0)]
    [InlineData(SessionId, -5)]
    public async Task MarkAsync_EmptySessionOrNoLifetime_ReturnsFalseWithoutWriting(string? sessionId, int seconds)
    {
        var cache = new Mock<ICacheClient>(MockBehavior.Strict);

        var marked = await RevokedSessions.MarkAsync(cache.Object, sessionId, TimeSpan.FromSeconds(seconds));

        Assert.False(marked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsRevokedAsync_ReturnsWhetherTheMarkExists(bool exists)
    {
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.KeyExistsAsync(RevokedSessions.KeyFor(SessionId))).ReturnsAsync(exists);

        Assert.Equal(exists, await RevokedSessions.IsRevokedAsync(cache.Object, SessionId));
    }

    [Fact]
    public async Task IsRevokedAsync_EmptySessionId_IsNeverRevoked()
    {
        var cache = new Mock<ICacheClient>(MockBehavior.Strict);

        Assert.False(await RevokedSessions.IsRevokedAsync(cache.Object, " "));
    }

    [Fact]
    public async Task Helpers_RequireACacheClient()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => RevokedSessions.MarkAsync(null!, SessionId, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RevokedSessions.IsRevokedAsync(null!, SessionId));
    }

    // ---------- Token check (OnTokenValidated) ----------

    [Fact]
    public async Task OnTokenValidated_RefusesTheToken_WhenItsSessionIsRevoked()
    {
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.KeyExistsAsync(RevokedSessions.KeyFor(SessionId))).ReturnsAsync(true);
        var (context, identity) = CreateValidatedContext(cache.Object, SessionId);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.NotNull(context.Result);
        Assert.False(context.Result!.Succeeded);
        Assert.Equal("session_revoked", context.Result.Failure?.Message);
        Assert.Null(identity.FindFirst(BlocksContext.REQUEST_URI_CLAIM));
    }

    [Fact]
    public async Task OnTokenValidated_AcceptsTheToken_WhenItsSessionIsNotRevoked()
    {
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.KeyExistsAsync(RevokedSessions.KeyFor(SessionId))).ReturnsAsync(false);
        var (context, identity) = CreateValidatedContext(cache.Object, SessionId);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.Null(context.Result);
        Assert.NotNull(identity.FindFirst(BlocksContext.REQUEST_URI_CLAIM));
        cache.Verify(c => c.KeyExistsAsync(RevokedSessions.KeyFor(SessionId)), Times.Once);
    }

    [Fact]
    public async Task OnTokenValidated_SkipsTheLookup_WhenTheTokenHasNoSessionId()
    {
        var cache = new Mock<ICacheClient>();
        var (context, _) = CreateValidatedContext(cache.Object, sessionId: null);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.Null(context.Result);
        cache.Verify(c => c.KeyExistsAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task OnTokenValidated_AcceptsTheToken_WhenTheLookupFails()
    {
        // Fails open: a cache outage must not sign every user out.
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.KeyExistsAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("cache unavailable"));
        var (context, identity) = CreateValidatedContext(cache.Object, SessionId);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.Null(context.Result);
        Assert.NotNull(identity.FindFirst(BlocksContext.REQUEST_URI_CLAIM));
    }

    [Fact]
    public async Task OnTokenValidated_AcceptsTheToken_WhenNoCacheClientIsRegistered()
    {
        var (context, _) = CreateValidatedContext(cacheClient: null, SessionId);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.Null(context.Result);
    }

    private static (TokenValidatedContext Context, ClaimsIdentity Identity) CreateValidatedContext(ICacheClient? cacheClient, string? sessionId)
    {
        var claims = new List<Claim>
        {
            new(BlocksContext.TENANT_ID_CLAIM, "tenant-1"),
            new(BlocksContext.USER_ID_CLAIM, "user-1")
        };
        if (sessionId != null)
        {
            claims.Add(new Claim(BlocksContext.SESSION_ID_CLAIM, sessionId));
        }

        var identity = new ClaimsIdentity(claims, "Bearer");

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddSingleton(new Mock<ITenants>().Object);
        services.AddSingleton(new Mock<IHttpClientFactory>().Object);
        if (cacheClient != null)
        {
            services.AddSingleton(cacheClient);
        }

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("example.local");
        httpContext.Request.Path = "/api/orders";
        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = httpContext };

        var context = new TokenValidatedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(identity)
        };

        return (context, identity);
    }

    private static JwtBearerEvents BuildJwtEvents()
    {
        var type = Type.GetType(ExtensionTypeName);
        Assert.NotNull(type);
        var method = type!.GetMethod("ConfigureAuthenticationInternal", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        method!.Invoke(null, [services]);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(options.Events);
        return options.Events;
    }
}
