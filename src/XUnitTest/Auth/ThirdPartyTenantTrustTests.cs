using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace XUnitTest.Auth;

/// <summary>
/// The tenant a third-party token is validated against must come from the request, never from the
/// token.
/// </summary>
/// <remarks>
/// A third-party token is minted outside this system, so a <c>tenant_id</c> claim inside it is
/// attacker-controlled. Honouring it would let a token from an issuer that one tenant trusts be
/// pointed at a different tenant, picking up that tenant's providers, claim mapping and roles.
/// </remarks>
[Collection("BlocksAuthStaticState")]
public class ThirdPartyTenantTrustTests
{
    private const string VictimTenant = "VICTIM-TENANT";
    private const string CallerTenant = "CALLER-TENANT";

    private static readonly Type Target =
        Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis")!;

    private static string TokenClaimingTenant(string tenantId)
    {
        var jwt = new JwtSecurityToken(
            issuer: "https://dev-kqgrj13jhskombl1.us.auth0.com/",
            claims: [new Claim(BlocksContext.TENANT_ID_CLAIM, tenantId), new Claim("sub", "auth0|abc")],
            expires: DateTime.UtcNow.AddHours(1));

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static (ResultContext<JwtBearerOptions> Context, Mock<ITenants> Tenants) Harness(
        string? blocksKeyHeader)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICryptoService>(new CryptoService());

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        if (blocksKeyHeader is not null)
        {
            httpContext.Request.Headers[BlocksConstants.BlocksKey] = blocksKeyHeader;
        }

        var context = new TokenValidatedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        return (context, new Mock<ITenants>());
    }

    private static async Task<bool> TryFallbackAsync(
        ResultContext<JwtBearerOptions> context,
        ITenants tenants,
        string token,
        string? tenantId)
    {
        var method = Target.GetMethod("TryFallbackAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task<bool>)method!.Invoke(
            null,
            [context, tenants, token, tenantId, new Mock<IHttpClientFactory>().Object, null])!;

        return await task;
    }

    [Fact]
    public async Task DoesNotTrustTheTenantClaimInsideAThirdPartyToken()
    {
        var (context, tenants) = Harness(blocksKeyHeader: null);

        // The token names a tenant. No header does. It must not be honoured.
        var accepted = await TryFallbackAsync(
            context,
            tenants.Object,
            TokenClaimingTenant(VictimTenant),
            tenantId: null);

        Assert.False(accepted);

        // Never looked the tenant up at all — the request declared none, so there is nothing to look up.
        tenants.Verify(t => t.GetTenantByID(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IgnoresATenantTheCallerAlreadyResolvedFromTheToken()
    {
        // HandleMessageReceived resolves a tenant for the primary path and may have taken it from
        // the token. The third-party path must re-derive it from the request rather than inherit it.
        var (context, tenants) = Harness(blocksKeyHeader: null);

        var accepted = await TryFallbackAsync(
            context,
            tenants.Object,
            TokenClaimingTenant(VictimTenant),
            tenantId: VictimTenant);

        Assert.False(accepted);
        tenants.Verify(t => t.GetTenantByID(VictimTenant), Times.Never);
    }

    [Fact]
    public async Task UsesTheHeaderTenant_WhenTheTokenClaimsADifferentOne()
    {
        var (context, tenants) = Harness(blocksKeyHeader: CallerTenant);

        // Tenant is unknown to the registry, so this stops right after the lookup — which is all
        // this test needs to see: which tenant was looked up.
        tenants.Setup(t => t.GetTenantByID(It.IsAny<string>())).Returns((Blocks.Genesis.Tenant?)null);

        var accepted = await TryFallbackAsync(
            context,
            tenants.Object,
            TokenClaimingTenant(VictimTenant),
            tenantId: VictimTenant);

        Assert.False(accepted);
        tenants.Verify(t => t.GetTenantByID(CallerTenant), Times.Once);
        tenants.Verify(t => t.GetTenantByID(VictimTenant), Times.Never);
    }
}
