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
/// The stamps IAM writes when a user loses access, an organization is disabled or a session switches
/// organization, and the token check that refuses an access token issued before a stamp.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class AccessStampsTests
{
    private const string ExtensionTypeName = "Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis";
    private const string TenantId = "tenant-1";
    private const string UserId = "user-1";
    private const string OrganizationId = "org-a";
    private const string SessionId = "5301086ffb2147909952eb4b1d50e8ef";
    private const string AccessChanged = "access_changed";
    private const long IssuedAt = 1_800_000_000;

    [Fact]
    public async Task StampAsync_WritesTheCurrentUnixTime_ForTheWholeLifetime()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string? written = null;
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.AddStringValueAsync(AccessStamps.UserKey(TenantId, UserId), It.IsAny<string>(), 901))
            .Callback<string, string, long>((_, value, _) => written = value)
            .ReturnsAsync(true);

        var stamped = await AccessStamps.StampAsync(cache.Object, AccessStamps.UserKey(TenantId, UserId), TimeSpan.FromSeconds(900.2));

        Assert.True(stamped);
        var stampedAt = long.Parse(written!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(stampedAt, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task OnTokenValidated_RefusesATokenIssuedBeforeTheUserLostAccess()
    {
        var cache = CacheWith(AccessStamps.UserKey(TenantId, UserId), IssuedAt + 1);
        var context = CreateValidatedContext(cache.Object, OrganizationId);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.Equal(AccessChanged, context.Result?.Failure?.Message);
    }

    [Fact]
    public async Task OnTokenValidated_AcceptsATokenMintedInTheSecondOfTheStamp()
    {
        // The refresh that answers a stamp is minted in that same second and must not be refused again.
        var cache = CacheWith(AccessStamps.UserKey(TenantId, UserId), IssuedAt);
        var context = CreateValidatedContext(cache.Object, OrganizationId);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task OnTokenValidated_RefusesATokenOfADisabledOrganization_ButNotOfAnotherOne()
    {
        var cache = CacheWith(AccessStamps.OrganizationKey(TenantId, OrganizationId), IssuedAt + 1);
        var inDisabledOrg = CreateValidatedContext(cache.Object, OrganizationId);
        var inOtherOrg = CreateValidatedContext(cache.Object, "org-b");

        await BuildJwtEvents().OnTokenValidated(inDisabledOrg);
        await BuildJwtEvents().OnTokenValidated(inOtherOrg);

        Assert.Equal(AccessChanged, inDisabledOrg.Result?.Failure?.Message);
        Assert.Null(inOtherOrg.Result);
    }

    [Fact]
    public async Task OnTokenValidated_RefusesAnOlderTokenOfASessionThatSwitchedOrganization()
    {
        // One organization at a time: a tab still holding the token of the previous organization refreshes.
        var cache = CacheWith(AccessStamps.SessionKey(SessionId), IssuedAt + 1);
        var context = CreateValidatedContext(cache.Object, OrganizationId);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.Equal(AccessChanged, context.Result?.Failure?.Message);
    }

    [Fact]
    public async Task OnTokenValidated_NeverLooksUpTheDefaultOrganization()
    {
        // "default" is the tenant-wide scope sentinel, not an organization that can be disabled.
        var cache = new Mock<ICacheClient>();
        var context = CreateValidatedContext(cache.Object, "default");

        await BuildJwtEvents().OnTokenValidated(context);

        cache.Verify(c => c.GetStringValueAsync(AccessStamps.OrganizationKey(TenantId, "default")), Times.Never);
    }

    [Fact]
    public async Task OnTokenValidated_AcceptsTheToken_WhenTheStampLookupFails()
    {
        // Fails open like the revoked-session check: the refresh still re-reads membership.
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.GetStringValueAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("cache unavailable"));
        var context = CreateValidatedContext(cache.Object, OrganizationId);

        await BuildJwtEvents().OnTokenValidated(context);

        Assert.Null(context.Result);
    }

    private static Mock<ICacheClient> CacheWith(string key, long stampedAt)
    {
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.GetStringValueAsync(It.IsAny<string>())).ReturnsAsync((string)null!);
        cache.Setup(c => c.GetStringValueAsync(key)).ReturnsAsync(stampedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return cache;
    }

    private static TokenValidatedContext CreateValidatedContext(ICacheClient cacheClient, string organizationId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(BlocksContext.TENANT_ID_CLAIM, TenantId),
            new Claim(BlocksContext.USER_ID_CLAIM, UserId),
            new Claim(BlocksContext.ORGANIZATION_ID_CLAIM, organizationId),
            new Claim(BlocksContext.SESSION_ID_CLAIM, SessionId),
            new Claim(BlocksContext.ISSUED_AT_TIME_CLAIM, IssuedAt.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ], "Bearer");

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddSingleton(new Mock<ITenants>().Object);
        services.AddSingleton(new Mock<IHttpClientFactory>().Object);
        services.AddSingleton(cacheClient);

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("example.local");
        httpContext.Request.Path = "/api/orders";
        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = httpContext };

        return new TokenValidatedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(identity)
        };
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
