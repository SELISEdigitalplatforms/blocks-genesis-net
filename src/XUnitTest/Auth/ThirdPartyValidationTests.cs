using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Moq;
using System.Net.Http;
using System.Reflection;
using Xunit;

namespace XUnitTest.Auth;

/// <summary>
/// Covers the provider-driven half of third-party validation: choosing a key source from the
/// configured algorithms, pinning them, and turning a stored cipher back into an HMAC key.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class ThirdPartyValidationTests
{
    private const string Issuer = "https://dev-kqgrj13jhskombl1.us.auth0.com/";
    private const string Salt = "9f2c1d4ea77b40c8a1e3b6d5c0f81a72";

    private static readonly Type Target =
        Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis")!;

    private static Blocks.Genesis.Tenant TenantWithSalt(string salt = Salt) => new()
    {
        ItemId = "tenant-1",
        TenantId = "tenant-1",
        TenantSalt = salt,
        DbConnectionString = "mongodb://localhost",
        JwtTokenParameters = new JwtTokenParameters
        {
            PrivateCertificatePassword = string.Empty,
            IssueDate = DateTime.UtcNow
        }
    };

    private static ThirdPartyJwtProvider Provider(
        JwtSigningAlgorithm[] algorithms,
        string jwksUrl = "",
        string cipher = "") => new()
        {
            Key = "auth0-a",
            ProviderName = "Auth0",
            IsActive = true,
            Issuer = Issuer,
            Audiences = ["api-a"],
            Algorithms = [.. algorithms],
            JwksUrl = jwksUrl,
            SigningSecretCipher = cipher
        };

    private static TokenValidatedContext Context(ICryptoService? crypto = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(crypto ?? new CryptoService());

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        return new TokenValidatedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());
    }

    private static async Task<TokenValidationParameters?> BuildAsync(
        Blocks.Genesis.Tenant tenant,
        ThirdPartyJwtProvider provider,
        TokenValidatedContext context)
    {
        var method = Target.GetMethod("BuildProviderValidationParametersAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task<TokenValidationParameters?>)method!.Invoke(
            null,
            [tenant, provider, context, new Mock<IHttpClientFactory>().Object])!;

        return await task;
    }

    [Fact]
    public async Task Rejects_WhenNoAlgorithmIsConfigured()
    {
        var result = await BuildAsync(TenantWithSalt(), Provider([]), Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_UnspecifiedAlgorithm()
    {
        // A row written before the field existed deserializes to Unspecified. For something that
        // selects a key source, unset must be refused rather than defaulted to RS256.
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.Unspecified]),
            Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_WhenAlgorithmsMixKeySources()
    {
        // One provider cannot have both a JWKS and a shared secret: that is two independent
        // signing authorities behind a single claim mapping.
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.RS256, JwtSigningAlgorithm.HS256]),
            Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_AsymmetricProviderWithNoJwksUrl()
    {
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.RS256], jwksUrl: ""),
            Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_SymmetricProviderWithNoSecret()
    {
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.HS256], cipher: ""),
            Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task BuildsSymmetricKey_FromTheStoredCipher()
    {
        var crypto = new CryptoService();
        var cipher = crypto.Encrypt("the-shared-signing-secret", Salt);

        var result = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.HS256], cipher: cipher),
            Context(crypto));

        Assert.NotNull(result);
        Assert.IsType<SymmetricSecurityKey>(result!.IssuerSigningKey);

        // Pinned from configuration. Without this the validator accepts whatever the key type
        // supports, which is wider than any provider needs.
        Assert.Equal(["HS256"], result.ValidAlgorithms);
        Assert.True(result.ValidateIssuer);
        Assert.Equal(Issuer, result.ValidIssuer);
        Assert.True(result.ValidateAudience);
        Assert.True(result.ValidateLifetime);
    }

    [Fact]
    public async Task Rejects_WhenTheCipherWasEncryptedUnderADifferentSalt()
    {
        // TenantSalt is the key material, so regenerating it makes every stored secret for the
        // tenant undecryptable — and the symptom would otherwise be an unexplained 401.
        var crypto = new CryptoService();
        var cipher = crypto.Encrypt("the-shared-signing-secret", Salt);

        var result = await BuildAsync(
            TenantWithSalt("a-different-salt-entirely"),
            Provider([JwtSigningAlgorithm.HS256], cipher: cipher),
            Context(crypto));

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_WhenTheTenantHasNoSalt()
    {
        var crypto = new CryptoService();
        var cipher = crypto.Encrypt("the-shared-signing-secret", Salt);

        var result = await BuildAsync(
            TenantWithSalt(string.Empty),
            Provider([JwtSigningAlgorithm.HS256], cipher: cipher),
            Context(crypto));

        Assert.Null(result);
    }

    [Fact]
    public async Task AudienceValidationIsOff_WhenNoAudienceIsConfigured()
    {
        // Documents the hazard: an empty Audiences list accepts any audience from that issuer.
        var crypto = new CryptoService();
        var provider = Provider([JwtSigningAlgorithm.HS256], cipher: crypto.Encrypt("s", Salt));
        provider.Audiences = [];

        var result = await BuildAsync(TenantWithSalt(), provider, Context(crypto));

        Assert.NotNull(result);
        Assert.False(result!.ValidateAudience);
    }

    [Theory]
    [InlineData(JwtSigningAlgorithm.HS256, true)]
    [InlineData(JwtSigningAlgorithm.HS384, true)]
    [InlineData(JwtSigningAlgorithm.HS512, true)]
    [InlineData(JwtSigningAlgorithm.RS256, false)]
    [InlineData(JwtSigningAlgorithm.ES256, false)]
    [InlineData(JwtSigningAlgorithm.PS512, false)]
    public void IsSymmetric_IdentifiesTheHmacFamily(JwtSigningAlgorithm algorithm, bool expected)
    {
        Assert.Equal(expected, algorithm.IsSymmetric());
    }

    [Fact]
    public void WireNames_MatchTheJwtAlgHeaderValues()
    {
        Assert.Equal("RS256", JwtSigningAlgorithm.RS256.ToWireName());
        Assert.Equal("ES384", JwtSigningAlgorithm.ES384.ToWireName());
        Assert.Equal("PS512", JwtSigningAlgorithm.PS512.ToWireName());
        Assert.Equal("HS256", JwtSigningAlgorithm.HS256.ToWireName());
        Assert.Equal(string.Empty, JwtSigningAlgorithm.Unspecified.ToWireName());
    }

    [Fact]
    public void WireNames_DropUnspecifiedAndDeduplicate()
    {
        JwtSigningAlgorithm[] algorithms =
        [
            JwtSigningAlgorithm.RS256,
            JwtSigningAlgorithm.RS256,
            JwtSigningAlgorithm.Unspecified
        ];

        Assert.Equal(["RS256"], algorithms.ToWireNames());
        Assert.Empty(((IEnumerable<JwtSigningAlgorithm>?)null).ToWireNames());
    }

    [Fact]
    public void IsSingleKeySource_RequiresOneFamilyAndNoUnspecified()
    {
        Assert.True(new[] { JwtSigningAlgorithm.RS256, JwtSigningAlgorithm.ES256 }.IsSingleKeySource());
        Assert.True(new[] { JwtSigningAlgorithm.HS256, JwtSigningAlgorithm.HS512 }.IsSingleKeySource());

        Assert.False(new[] { JwtSigningAlgorithm.RS256, JwtSigningAlgorithm.HS256 }.IsSingleKeySource());
        Assert.False(new[] { JwtSigningAlgorithm.Unspecified }.IsSingleKeySource());
        Assert.False(Array.Empty<JwtSigningAlgorithm>().IsSingleKeySource());
        Assert.False(((IReadOnlyCollection<JwtSigningAlgorithm>?)null).IsSingleKeySource());
    }
}
