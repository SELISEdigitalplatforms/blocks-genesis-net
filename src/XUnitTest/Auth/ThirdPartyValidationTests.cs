using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Moq;
using System.Net.Http;
using System.Reflection;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace XUnitTest.Auth;

/// <summary>
/// Covers the provider-driven half of third-party validation: choosing a key source from the
/// configured algorithms, pinning them, and turning a stored cipher back into an HMAC key.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class ThirdPartyValidationTests : IDisposable
{
    private const string Issuer = "https://dev-kqgrj13jhskombl1.us.auth0.com/";
    private const string Salt = "9f2c1d4ea77b40c8a1e3b6d5c0f81a72";

    private readonly List<string> _temporaryFiles = [];

    public void Dispose()
    {
        foreach (var path in _temporaryFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test run over.
            }
        }

        GC.SuppressFinalize(this);
    }

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
        string cipher = "",
        string certificatePath = "",
        string certificatePasswordCipher = "",
        string? issuer = Issuer,
        string[]? audiences = null) => new()
        {
            Key = "auth0-a",
            TenantId = "tenant-1",
            ProviderName = "Auth0",
            IsActive = true,
            Issuer = issuer ?? string.Empty,
            Audiences = [.. audiences ?? ["api-a"]],
            Algorithms = [.. algorithms],
            JwksUrl = jwksUrl,
            SigningSecretCipher = cipher,
            PublicCertificatePath = certificatePath,
            PublicCertificatePasswordCipher = certificatePasswordCipher
        };

    /// <summary>How the certificate file on disk is encoded.</summary>
    /// <remarks>
    /// PEM and DER are the same certificate in two encodings, and both routinely carry a
    /// <c>.crt</c> extension -- so the extension says nothing about which one arrived. PEM is by
    /// far the more common of the two in the wild, which is why it is covered explicitly rather
    /// than assumed equivalent to DER.
    /// </remarks>
    private enum CertificateEncoding
    {
        /// <summary>Base64 between BEGIN/END lines. What most providers hand over as a .crt.</summary>
        Pem,

        /// <summary>Raw ASN.1 DER bytes.</summary>
        Der,

        /// <summary>A PKCS#12 container, the only form a passphrase can protect.</summary>
        Pkcs12
    }

    /// <summary>
    /// Writes a self-signed certificate to a temp file and returns it with its path.
    /// </summary>
    /// <remarks>
    /// Generated per test rather than committed as a fixture: a checked-in certificate expires and
    /// turns into a failure that looks like a code regression. The certificate is returned as well
    /// as written so a test can sign a token with the matching private key and prove the resolved
    /// key actually verifies a signature, rather than only that a key came back.
    /// </remarks>
    private (string Path, X509Certificate2 Certificate) WriteCertificate(
        CertificateEncoding encoding,
        string? password = null)
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=third-party-validation-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        var extension = encoding == CertificateEncoding.Pkcs12 ? ".pfx" : ".crt";
        var path = Path.Combine(Path.GetTempPath(), $"blocks-idp-test-{Guid.NewGuid():N}{extension}");

        switch (encoding)
        {
            case CertificateEncoding.Pem:
                File.WriteAllText(path, certificate.ExportCertificatePem());
                break;
            case CertificateEncoding.Der:
                File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
                break;
            default:
                File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
                break;
        }

        _temporaryFiles.Add(path);
        return (path, certificate);
    }

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
    public async Task Rejects_AsymmetricProviderWithNoKeySource()
    {
        // Neither of the two asymmetric key sources configured, so there is nothing to verify a
        // signature against.
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.RS256], jwksUrl: "", certificatePath: ""),
            Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task BuildsCertificateKey_FromAPemCertificateFile()
    {
        // The real-world shape: a provider with no JWKS hands over a PEM .crt holding one public
        // key. Covered separately from DER because the two are different bytes behind the same
        // extension, and only the loader decides whether that distinction matters.
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider(
                [JwtSigningAlgorithm.RS256],
                certificatePath: WriteCertificate(CertificateEncoding.Pem).Path),
            Context());

        Assert.NotNull(result);
        Assert.IsType<X509SecurityKey>(result!.IssuerSigningKey);

        // Pinned from configuration, never from the token's own alg header — the same guarantee
        // the JWKS and HMAC paths give.
        Assert.Equal(["RS256"], result.ValidAlgorithms);
        Assert.True(result.ValidateIssuer);
        Assert.Equal(Issuer, result.ValidIssuer);
        Assert.True(result.ValidateAudience);
        Assert.True(result.ValidateLifetime);
    }

    [Fact]
    public async Task BuildsCertificateKey_FromADerCertificateFile()
    {
        // The same certificate as the PEM case, in the other encoding, under the same extension.
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider(
                [JwtSigningAlgorithm.RS256],
                certificatePath: WriteCertificate(CertificateEncoding.Der).Path),
            Context());

        Assert.NotNull(result);
        Assert.IsType<X509SecurityKey>(result!.IssuerSigningKey);
    }

    [Fact]
    public async Task ResolvedCertificateKey_ActuallyVerifiesATokenSignedByThatCertificate()
    {
        // The end the feature exists for. Every other certificate test proves a key came back;
        // this one proves the key that came back is the one that validates the provider's tokens.
        var (path, certificate) = WriteCertificate(CertificateEncoding.Pem);
        using var signingCertificate = certificate;

        var parameters = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.RS256], certificatePath: path),
            Context());

        Assert.NotNull(parameters);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(new JwtSecurityToken(
            issuer: Issuer,
            audience: "api-a",
            claims: [new Claim("sub", "user-42")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new X509SecurityKey(signingCertificate),
                SecurityAlgorithms.RsaSha256)));

        var principal = handler.ValidateToken(token, parameters, out _);

        Assert.True(principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Rejects_ATokenSignedByADifferentKey()
    {
        // Pins that the certificate is doing the verifying. Without this, a test suite that only
        // ever presents valid tokens cannot tell a pinned key from no key at all.
        var (path, _) = WriteCertificate(CertificateEncoding.Pem);
        var (_, impostor) = WriteCertificate(CertificateEncoding.Pem);
        using var impostorCertificate = impostor;

        var parameters = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.RS256], certificatePath: path),
            Context());

        Assert.NotNull(parameters);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(new JwtSecurityToken(
            issuer: Issuer,
            audience: "api-a",
            claims: [new Claim("sub", "user-42")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new X509SecurityKey(impostorCertificate),
                SecurityAlgorithms.RsaSha256)));

        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(
            () => handler.ValidateToken(token, parameters, out _));
    }

    [Fact]
    public async Task BuildsCertificateKey_FromAPassphraseProtectedPkcs12()
    {
        var crypto = new CryptoService();
        const string passphrase = "the-pfx-passphrase";

        var result = await BuildAsync(
            TenantWithSalt(),
            Provider(
                [JwtSigningAlgorithm.RS256],
                certificatePath: WriteCertificate(CertificateEncoding.Pkcs12, passphrase).Path,
                certificatePasswordCipher: crypto.Encrypt(passphrase, Salt)),
            Context(crypto));

        Assert.NotNull(result);
        Assert.IsType<X509SecurityKey>(result!.IssuerSigningKey);
    }

    [Fact]
    public async Task Rejects_WhenTheStoredPassphraseDoesNotMatchThePkcs12()
    {
        // The everyday operator mistake. It must fail as a configuration fault rather than be
        // reported against whatever token happened to arrive.
        var crypto = new CryptoService();

        var result = await BuildAsync(
            TenantWithSalt(),
            Provider(
                [JwtSigningAlgorithm.RS256],
                certificatePath: WriteCertificate(CertificateEncoding.Pkcs12, "the-real-passphrase").Path,
                certificatePasswordCipher: crypto.Encrypt("not-the-passphrase", Salt)),
            Context(crypto));

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_WhenThePassphraseWasEncryptedUnderADifferentSalt()
    {
        // Same hazard as the HMAC secret: regenerating TenantSalt orphans every stored cipher.
        var crypto = new CryptoService();
        const string passphrase = "the-pfx-passphrase";

        var result = await BuildAsync(
            TenantWithSalt("a-different-salt-entirely"),
            Provider(
                [JwtSigningAlgorithm.RS256],
                certificatePath: WriteCertificate(CertificateEncoding.Pkcs12, passphrase).Path,
                certificatePasswordCipher: crypto.Encrypt(passphrase, Salt)),
            Context(crypto));

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_WhenTheCertificateFileCannotBeRead()
    {
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider(
                [JwtSigningAlgorithm.RS256],
                certificatePath: Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.crt")),
            Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task Rejects_WhenTheCertificatePathIsNeitherAUrlNorAFile()
    {
        // A relative path or a malformed URL: the loader can do nothing with either.
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider([JwtSigningAlgorithm.RS256], certificatePath: "certs/provider.crt"),
            Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task AcceptsAnHmacTokenCarryingNeitherIssuerNorAudience()
    {
        // The shape a real third party sent: HS256, a shared secret, and a payload with no `iss`
        // and no `aud` at all. Both checks switch themselves off from the provider's own blank
        // configuration, so the signature and lifetime are what the token stands or falls on.
        var crypto = new CryptoService();
        const string secret = "the-shared-signing-secret-at-least-32-bytes";

        var parameters = await BuildAsync(
            TenantWithSalt(),
            Provider(
                [JwtSigningAlgorithm.HS256],
                cipher: crypto.Encrypt(secret, Salt),
                issuer: null,
                audiences: []),
            Context(crypto));

        Assert.NotNull(parameters);
        Assert.False(parameters!.ValidateIssuer);
        Assert.False(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.Equal(["HS256"], parameters.ValidAlgorithms);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(new JwtSecurityToken(
            issuer: null,
            audience: null,
            claims: [new Claim("sub", "f2e99db9-6cb7-47bf-9d53-180153213fe4")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(59),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                SecurityAlgorithms.HmacSha256)));

        var principal = handler.ValidateToken(token, parameters, out _);

        Assert.True(principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task RejectsAnHmacTokenSignedWithTheWrongSecret()
    {
        // Turning issuer and audience validation off leaves the signature as the only thing
        // standing between a caller and a principal, so it is worth pinning that it still bites.
        var crypto = new CryptoService();

        var parameters = await BuildAsync(
            TenantWithSalt(),
            Provider(
                [JwtSigningAlgorithm.HS256],
                cipher: crypto.Encrypt("the-real-shared-signing-secret-32-bytes", Salt),
                issuer: null,
                audiences: []),
            Context(crypto));

        Assert.NotNull(parameters);

        var handler = new JwtSecurityTokenHandler();
        var forged = handler.WriteToken(new JwtSecurityToken(
            claims: [new Claim("sub", "attacker")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-secret-32-bytes")),
                SecurityAlgorithms.HmacSha256)));

        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(
            () => handler.ValidateToken(forged, parameters!, out _));
    }

    [Fact]
    public async Task RejectsAnExpiredIssuerlessHmacToken()
    {
        // Lifetime is the other check that survives, and their tokens live one hour.
        var crypto = new CryptoService();
        const string secret = "the-shared-signing-secret-at-least-32-bytes";

        var parameters = await BuildAsync(
            TenantWithSalt(),
            Provider(
                [JwtSigningAlgorithm.HS256],
                cipher: crypto.Encrypt(secret, Salt),
                issuer: null,
                audiences: []),
            Context(crypto));

        var handler = new JwtSecurityTokenHandler();
        var stale = handler.WriteToken(new JwtSecurityToken(
            claims: [new Claim("sub", "user-1")],
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                SecurityAlgorithms.HmacSha256)));

        Assert.Throws<SecurityTokenExpiredException>(
            () => handler.ValidateToken(stale, parameters!, out _));
    }

    [Theory]
    [InlineData(JwtSigningAlgorithm.ES256)]
    [InlineData(JwtSigningAlgorithm.PS512)]
    public async Task CertificateKeySource_ServesEveryAsymmetricFamily(JwtSigningAlgorithm algorithm)
    {
        // The key source is selected by family, not by the specific member, so every asymmetric
        // algorithm reaches the certificate the same way RS256 does.
        var result = await BuildAsync(
            TenantWithSalt(),
            Provider([algorithm], certificatePath: WriteCertificate(CertificateEncoding.Pem).Path),
            Context());

        Assert.NotNull(result);
        Assert.IsType<X509SecurityKey>(result!.IssuerSigningKey);
        Assert.Equal([algorithm.ToWireName()], result.ValidAlgorithms);
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
