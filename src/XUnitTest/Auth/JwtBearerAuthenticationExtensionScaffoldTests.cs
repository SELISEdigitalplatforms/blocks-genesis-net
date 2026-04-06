using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MongoDB.Bson;
using Moq;
using OpenTelemetry;
using StackExchange.Redis;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

        var resultTask = (Task<bool>)method!.Invoke(null, [context, new Moq.Mock<ITenants>().Object, string.Empty, null, new Moq.Mock<IHttpClientFactory>().Object, null])!;
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

    [Fact]
    public void RequestItemHelpers_ShouldRoundTripAccessTokenAndTenantId()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var setToken = type!.GetMethod("SetRequestAccessToken", BindingFlags.NonPublic | BindingFlags.Static);
        var getToken = type.GetMethod("GetRequestAccessToken", BindingFlags.NonPublic | BindingFlags.Static);
        var setTenant = type.GetMethod("SetRequestTenantId", BindingFlags.NonPublic | BindingFlags.Static);
        var getTenant = type.GetMethod("GetRequestTenantId", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(setToken);
        Assert.NotNull(getToken);
        Assert.NotNull(setTenant);
        Assert.NotNull(getTenant);

        var httpContext = new DefaultHttpContext();

        setToken!.Invoke(null, [httpContext, "abc-token"]);
        setTenant!.Invoke(null, [httpContext, "tenant-x"]);

        var token = (string)getToken!.Invoke(null, [httpContext])!;
        var tenant = (string?)getTenant!.Invoke(null, [httpContext]);

        Assert.Equal("abc-token", token);
        Assert.Equal("tenant-x", tenant);
    }

    [Fact]
    public void GetContextWithoutToken_ShouldSanitizeSensitiveTokens()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("GetContextWithoutToken", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var source = BlocksContext.Create(
            "tenant-a", ["admin"], "user-a", true, "/api", "org-a", DateTime.UtcNow,
            "u@a.com", ["read"], "user-a", "123", "User A", "oauth-secret", "refresh-secret", "tenant-a");

        var sanitized = (BlocksContext)method!.Invoke(null, [source])!;

        Assert.Equal("tenant-a", sanitized.TenantId);
        Assert.Equal("user-a", sanitized.UserId);
        Assert.Equal(string.Empty, sanitized.OAuthToken);
        Assert.Equal(string.Empty, sanitized.RefreshToken);
    }

    [Fact]
    public void ExtractRolesFromClaim_ShouldReturnEmpty_WhenClaimMissingOrInvalid()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("ExtractRolesFromClaim", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var mapper = new BsonDocument { ["Roles"] = "realm_access.roles" };

        var identityWithoutClaim = new ClaimsIdentity();
        var missing = (string[])method!.Invoke(null, [identityWithoutClaim, mapper])!;
        Assert.Empty(missing);

        var identityWithInvalidJson = new ClaimsIdentity([new Claim("realm_access", "not-json")]);
        var invalid = (string[])method!.Invoke(null, [identityWithInvalidJson, mapper])!;
        Assert.Empty(invalid);

        var identityWithNonArrayRoles = new ClaimsIdentity([new Claim("realm_access", "{\"roles\":\"admin\"}")]);
        var nonArray = (string[])method!.Invoke(null, [identityWithNonArrayRoles, mapper])!;
        Assert.Empty(nonArray);
    }

    [Fact]
    public async Task LoadCertificateDataAsync_ShouldReadFromLocalFile()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("LoadCertificateDataAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var tempPath = Path.Combine(Path.GetTempPath(), $"cert-{Guid.NewGuid():N}.bin");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(tempPath, payload);
        try
        {
            var clientFactory = new Mock<IHttpClientFactory>();
            var task = (Task<byte[]?>)method!.Invoke(null, [tempPath, clientFactory.Object])!;
            var result = await task;
            Assert.Equal(payload, result);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public async Task LoadCertificateDataAsync_ShouldReturnNull_WhenHttpFetchThrows()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("LoadCertificateDataAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var httpClient = new HttpClient(new ThrowingHttpMessageHandler());
        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var task = (Task<byte[]?>)method!.Invoke(null, ["https://example.invalid/cert.pfx", clientFactory.Object])!;
        var result = await task;

        Assert.Null(result);
    }

    [Fact]
    public async Task CacheCertificateAsync_ShouldWriteWhenDaysRemainingIsPositive()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("CacheCertificateAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var validation = new JwtTokenParameters
        {
            Issuer = "issuer",
            Subject = "subject",
            Audiences = [],
            PublicCertificatePath = "path",
            PublicCertificatePassword = string.Empty,
            PrivateCertificatePassword = string.Empty,
            IssueDate = DateTime.UtcNow.AddDays(-1),
            CertificateValidForNumberOfDays = 10
        };

        var task = (Task)method!.Invoke(null, [cacheDb.Object, "key-a", new byte[] { 1, 2 }, validation])!;
        await task;

        cacheDb.Verify(db => db.StringSetAsync(
            It.Is<RedisKey>(k => (string)k! == "key-a"),
            It.IsAny<RedisValue>(),
            It.Is<TimeSpan?>(t => t.HasValue && t.Value.TotalDays > 0),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task CacheCertificateAsync_ShouldSkipWhenInvalidLifetimeConfiguration()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("CacheCertificateAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var cacheDb = new Mock<IDatabase>();

        var validation = new JwtTokenParameters
        {
            Issuer = "issuer",
            Subject = "subject",
            Audiences = [],
            PublicCertificatePath = "path",
            PublicCertificatePassword = string.Empty,
            PrivateCertificatePassword = string.Empty,
            IssueDate = DateTime.UtcNow,
            CertificateValidForNumberOfDays = 0
        };

        var task = (Task)method!.Invoke(null, [cacheDb.Object, "key-b", new byte[] { 9 }, validation])!;
        await task;

        cacheDb.Verify(db => db.StringSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task ConfigureTokenValidationAsync_ShouldFail_WhenTenantContextIsMissing()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("ConfigureTokenValidationAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = BuildRequestServicesForAuthTests();
        var scheme = new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var messageContext = new MessageReceivedContext(httpContext, scheme, options)
        {
            Token = "token-value"
        };

        var tenants = new Mock<ITenants>();
        var clientFactory = new Mock<IHttpClientFactory>();

        var task = (Task)method!.Invoke(null, [messageContext, tenants.Object, clientFactory.Object, null])!;
        await task;

        Assert.NotNull(messageContext.Result?.Failure);
        Assert.Contains("Tenant context not found", messageContext.Result!.Failure!.Message);
    }

    [Fact]
    public async Task ConfigureTokenValidationAsync_ShouldFail_WhenCertificateIsMissing()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("ConfigureTokenValidationAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = BuildRequestServicesForAuthTests();
        var scheme = new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var messageContext = new MessageReceivedContext(httpContext, scheme, options)
        {
            Token = "token-value"
        };

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-z")).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = "tenant-z",
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = null!,
            ThirdPartyJwtTokenParameters = null!
        });

        var clientFactory = new Mock<IHttpClientFactory>();

        var task = (Task)method!.Invoke(null, [messageContext, tenants.Object, clientFactory.Object, "tenant-z"])!;
        await task;

        Assert.NotNull(messageContext.Result?.Failure);
        Assert.Contains("Certificate not found", messageContext.Result!.Failure!.Message);
    }

    [Fact]
    public async Task ValidateTokenWithFallbackAsync_ShouldReturnFalse_ForInvalidToken()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("ValidateTokenWithFallbackAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var validationParams = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = false,
            ValidateLifetime = false
        };

        var task = (Task<bool>)method!.Invoke(null, ["invalid-token", validationParams, context])!;
        var result = await task;

        Assert.False(result);
    }

    [Fact]
    public async Task TryFallbackAsync_ShouldReturnFalse_WhenTenantConfigIsMissing()
    {
        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-no-fallback")).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = "tenant-no-fallback",
            ApplicationDomain = "app.local",
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
            ThirdPartyJwtTokenParameters = null!
        });

        var result = await InvokeTryFallbackAsync(context, tenants.Object, "dummy-token", "tenant-no-fallback", new Mock<IHttpClientFactory>().Object, null);
        Assert.False(result);
    }

    [Fact]
    public void StoreBlocksContextInActivity_ShouldSetBaggageAndSecurityTag()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("StoreBlocksContextInActivity", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var activity = new Activity("auth-test");
        activity.Start();

        var context = BlocksContext.Create(
            "tenant-1", ["role"], "user-1", true, "/x", "org", DateTime.UtcNow,
            "u@x.com", ["perm"], "user", "000", "User", "oauth", "refresh", "tenant-1");

        method!.Invoke(null, [context]);

        Assert.Equal("user-1", Baggage.GetBaggage("UserId"));
        Assert.Equal("true", Baggage.GetBaggage("IsAuthenticate"));
        Assert.Contains(activity.Tags, t => t.Key == "SecurityContext");
    }

    private static async Task<bool> InvokeTryFallbackAsync(
        TokenValidatedContext context,
        ITenants tenants,
        string token,
        string? tenantId,
        IHttpClientFactory httpClientFactory,
        Exception? ex)
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);

        var method = type!.GetMethod("TryFallbackAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task<bool>)method!.Invoke(null, [context, tenants, token, tenantId, httpClientFactory, ex])!;
        return await task;
    }

    private static IServiceProvider BuildRequestServicesForAuthTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<ICacheClient>(_ =>
        {
            var cacheClient = new Mock<ICacheClient>();
            cacheClient.Setup(c => c.CacheDatabase()).Returns(Mock.Of<IDatabase>());
            return cacheClient.Object;
        });

        return services.BuildServiceProvider();
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("network error");
        }
    }
}
