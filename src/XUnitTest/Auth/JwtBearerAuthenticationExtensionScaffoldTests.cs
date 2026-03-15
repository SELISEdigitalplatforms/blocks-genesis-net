using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XUnitTest.Auth;

public class JwtBearerAuthenticationExtensionScaffoldTests
{
    [Fact]
    public void InternalType_ShouldExist()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
    }

    [Fact]
    public void ExtractRolesFromClaim_ShouldExtractArrayValues_FromNestedJsonClaim()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var identity = new ClaimsIdentity(
        [
            new Claim("realm_access", "{\"roles\":[\"admin\",\"viewer\"]}")
        ], "Bearer");

        var mapper = new BsonDocument { ["Roles"] = "realm_access.roles" };
        var method = type!.GetMethod("ExtractRolesFromClaim", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var roles = (string[])method!.Invoke(null, [identity, mapper])!;

        Assert.Equal(["admin", "viewer"], roles);
    }

    [Fact]
    public async Task TryFallbackAsync_ShouldReturnFalse_WhenTokenIsEmpty()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var method = type!.GetMethod("TryFallbackAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var resultTask = (Task<bool>)method!.Invoke(null, [context, new Moq.Mock<ITenants>().Object, string.Empty, null])!;
        var result = await resultTask;

        Assert.False(result);
    }

    [Theory]
    [InlineData("realm_access.roles", "realm_access")]
    [InlineData("roles", "roles")]
    public void GetClaimObjectName_ShouldReturnLeadingSegment(string source, string expected)
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("GetClaimObjectName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, [source])!;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("realm_access.roles", "roles")]
    [InlineData("roles", "roles")]
    public void ExtactClaimProperty_ShouldReturnTrailingSegment(string source, string expected)
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("ExtactClaimProperty", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, [source])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtactClaimValue_ShouldReadDirectAndNestedClaims()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
        var method = type!.GetMethod("ExtactClaimValue", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var identity = new ClaimsIdentity(
        [
            new Claim("email", "user@example.com"),
            new Claim("realm_access", "{\"id\":\"abc-123\"}")
        ]);

        var direct = (string)method!.Invoke(null, [identity, "email"])!;
        var nested = (string)method!.Invoke(null, [identity, "realm_access.id"])!;

        Assert.Equal("user@example.com", direct);
        Assert.Equal("abc-123", nested);
    }

    [Fact]
    public void HandleTokenIssuer_ShouldAppendRequestUriAndTokenClaims()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
        var method = type!.GetMethod("HandleTokenIssuer", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var identity = new ClaimsIdentity();
        method!.Invoke(null, [identity, "/v1/orders", "jwt-token"]);

        Assert.Equal("/v1/orders", identity.FindFirst(BlocksContext.REQUEST_URI_CLAIM)?.Value);
        Assert.Equal("jwt-token", identity.FindFirst(BlocksContext.TOKEN_CLAIM)?.Value);
    }

    [Fact]
    public void CreateTokenValidationParameters_ShouldSetValidationFlags()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
        var method = type!.GetMethod("CreateTokenValidationParameters", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test-cert", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var tokenParams = new JwtTokenParameters
        {
            Issuer = "issuer",
            Subject = "subject",
            Audiences = ["api"],
            PublicCertificatePath = "path",
            PublicCertificatePassword = string.Empty,
            PrivateCertificatePassword = string.Empty,
            IssueDate = DateTime.UtcNow
        };

        var result = (Microsoft.IdentityModel.Tokens.TokenValidationParameters)method!.Invoke(null, [certificate, tokenParams])!;

        Assert.True(result.ValidateIssuerSigningKey);
        Assert.True(result.ValidateLifetime);
        Assert.Equal("issuer", result.ValidIssuer);
        Assert.True(result.ValidateAudience);
    }
}
