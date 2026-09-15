using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StackExchange.Redis;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace XUnitTest.Auth;

/// <summary>
/// What happens to a third-party token the fallback has <b>accepted</b>: the handler has to stop,
/// and the mapped context has to reach the endpoint.
/// </summary>
/// <remarks>
/// Both were silently lost before. The fallback reported success on a throwaway
/// <c>TokenValidatedContext</c> the framework never reads, so primary validation carried on and
/// answered 401 ("Bearer was not authenticated") behind an accepted token; and the mapped context
/// existed only in an AsyncLocal set inside the authentication event, which does not flow back out
/// to the middleware — the request reached the controller with no tenant and died at the first
/// database call.
/// </remarks>
[Collection("BlocksAuthStaticState")]
public class ThirdPartyFallbackAcceptanceTests
{
    private const string TenantId = "tenant-fallback";
    private const string Issuer = "https://dev-kqgrj13jhskombl1.us.auth0.com/";
    private const string Audience = "https://dev-kqgrj13jhskombl1.us.auth0.com/api/v2/";
    private const string Salt = "9f2c1d4ea77b40c8a1e3b6d5c0f81a72";
    private const string SigningSecret = "a-shared-signing-secret-of-at-least-32-bytes";

    private const string NsUserId = "https://myapp.example.com/user_id";
    private const string NsEmail = "https://myapp.example.com/email";
    private const string NsName = "https://myapp.example.com/name";
    private const string NsRoles = "https://myapp.example.com/roles";

    private const string Subject = "auth0|6aa7a5a9851b7c2b026f1112";

    [Fact]
    public async Task AcceptedThirdPartyToken_StopsTheHandler_AndCarriesTheMappedContextOnThePrincipal()
    {
        var context = await RunMessageReceivedAsync();

        // The whole point: Result is set on the context the framework holds, so the handler never
        // reaches primary validation. Null here is the 401-behind-an-accepted-token regression.
        Assert.NotNull(context.Result);
        Assert.True(context.Result!.Succeeded);

        // A subclass (CaseSensitiveClaimsIdentity) in practice, so assign rather than type-assert.
        var identity = Assert.IsAssignableFrom<ClaimsIdentity>(context.Result.Principal!.Identity);

        Assert.Equal(TenantId, identity.FindFirst(BlocksContext.TENANT_ID_CLAIM)?.Value);
        Assert.Equal($"{Subject}_external", identity.FindFirst(BlocksContext.USER_ID_CLAIM)?.Value);
        Assert.Equal("asif.rafeen.auth0@yopmail.com", identity.FindFirst(BlocksContext.EMAIL_CLAIM)?.Value);
        Assert.Equal(["TestAuth0Role"], identity.FindAll(identity.RoleClaimType).Select(c => c.Value));

        // The claims are the carrier, so the context the endpoint builds has to come out whole.
        var blocksContext = BlocksContext.CreateFromClaimsIdentity(identity);
        Assert.Equal(TenantId, blocksContext.TenantId);
        Assert.Equal($"{Subject}_external", blocksContext.UserId);
        Assert.Equal(["TestAuth0Role"], blocksContext.Roles);
    }

    [Fact]
    public async Task ProviderCannotNameItsOwnTenantOrPermissions()
    {
        // The token below mints tenant_id and permissions itself. Since the principal is now what
        // downstream reads, anything the mapping did not resolve has to be stripped -- otherwise a
        // provider configured on one tenant could assert another tenant and hand itself rights.
        var context = await RunMessageReceivedAsync();

        var identity = (ClaimsIdentity)context.Result!.Principal!.Identity!;

        Assert.Equal(TenantId, Assert.Single(identity.FindAll(BlocksContext.TENANT_ID_CLAIM)).Value);
        Assert.Empty(identity.FindAll(BlocksContext.PERMISSION_CLAIM));
        Assert.Empty(BlocksContext.CreateFromClaimsIdentity(identity).Permissions);
    }

    private static async Task<MessageReceivedContext> RunMessageReceivedAsync()
    {
        var crypto = new CryptoService();

        var tenant = new Blocks.Genesis.Tenant
        {
            ItemId = "bd7ddc51-8f8b-4732-bb28-a5a531b4166e",
            TenantId = TenantId,
            TenantSalt = Salt,
            IsThirdPartyJwtEnabled = true,
            Applications = [new Blocks.Genesis.Applications { Domain = "app.local" }],
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "SeliseBlocks",
                Subject = "subject",
                Audiences = [],
                PublicCertificatePath = "path",
                PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty,
                IssueDate = DateTime.UtcNow
            }
        };

        var provider = new ThirdPartyJwtProvider
        {
            Key = "abc",
            ProviderName = "Auth0",
            TenantId = TenantId,
            IsActive = true,
            Issuer = Issuer,
            Audiences = [Audience],
            Algorithms = [JwtSigningAlgorithm.HS256],
            SigningSecretCipher = crypto.Encrypt(SigningSecret, Salt),
            ClaimsMapping = new ThirdPartyClaimsMapping
            {
                UserId = NsUserId,
                Email = NsEmail,
                UserName = NsEmail,
                Name = NsName,
                Roles = NsRoles
            }
        };

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID(TenantId)).Returns(tenant);

        var store = new Mock<IThirdPartyJwtProviderStore>();
        store.Setup(s => s.GetActiveAsync(TenantId))
             .ReturnsAsync((IReadOnlyList<ThirdPartyJwtProvider>)[provider]);

        var http = CreateHttpContext(tenants.Object, store.Object, crypto);
        http.Request.Headers[BlocksConstants.BlocksKey] = TenantId;
        http.Request.Headers.Authorization = $"Bearer {CreateProviderToken()}";

        // OnMessageReceived only runs for endpoints that require authorization.
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute()), "protected"));

        var context = new MessageReceivedContext(
            http,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        try
        {
            await BuildJwtEvents().OnMessageReceived(context);
        }
        finally
        {
            BlocksContext.ClearContext();
        }

        return context;
    }

    private static string CreateProviderToken()
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Subject,
                [NsUserId] = Subject,
                [NsEmail] = "asif.rafeen.auth0@yopmail.com",
                [NsName] = "asif.rafeen.auth0@yopmail.com",
                [NsRoles] = new[] { "TestAuth0Role" },

                // Forged: neither may survive onto the principal.
                [BlocksContext.TENANT_ID_CLAIM] = "a-tenant-this-provider-does-not-own",
                [BlocksContext.PERMISSION_CLAIM] = new[] { "blocks-iam::iam::mutate-users" }
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningSecret)),
                SecurityAlgorithms.HmacSha256)
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static DefaultHttpContext CreateHttpContext(
        ITenants tenants,
        IThirdPartyJwtProviderStore store,
        ICryptoService crypto)
    {
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.CacheDatabase()).Returns(new Mock<IDatabase>().Object);

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddSingleton(tenants);
        services.AddSingleton(cacheClient.Object);
        services.AddSingleton(new Mock<IHttpClientFactory>().Object);
        services.AddSingleton(store);
        services.AddSingleton(crypto);

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private static JwtBearerEvents BuildJwtEvents()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("ConfigureAuthenticationInternal", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        method!.Invoke(null, [services]);

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.NotNull(options.Events);
        return options.Events;
    }
}
