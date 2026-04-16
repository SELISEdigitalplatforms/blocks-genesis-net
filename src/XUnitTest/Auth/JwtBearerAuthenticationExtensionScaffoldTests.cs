using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using System.Text;
using System.Text.Json;

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
    public async Task OnMessageReceived_ShouldFail_WhenTenantContextCannotBeResolved()
    {
        var tenants = new Mock<ITenants>();
        var cacheDb = new Mock<IDatabase>();
        var clientFactory = new Mock<IHttpClientFactory>();
        var events = BuildJwtEvents(tenants.Object, cacheDb.Object, clientFactory.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[BlocksConstants.AuthorizationHeaderName] = "Bearer not-a-jwt";

        var context = new MessageReceivedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        await events.OnMessageReceived(context);

        Assert.Equal("not-a-jwt", httpContext.Items["blocks.auth.accessToken"]?.ToString());
        Assert.NotNull(context.Result?.Failure);
        Assert.Contains("Tenant context not found", context.Result!.Failure!.Message);
    }

    [Fact]
    public async Task OnTokenValidated_ShouldAppendIssuerClaims()
    {
        var tenants = new Mock<ITenants>();
        var cacheDb = new Mock<IDatabase>();
        var clientFactory = new Mock<IHttpClientFactory>();
        var events = BuildJwtEvents(tenants.Object, cacheDb.Object, clientFactory.Object);

        var identity = new ClaimsIdentity(
        [
            new Claim(BlocksContext.TENANT_ID_CLAIM, "tenant-1"),
            new Claim(BlocksContext.USER_ID_CLAIM, "user-1")
        ], "Bearer");

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("example.local");
        httpContext.Request.Path = "/api/orders";
        httpContext.Request.Headers[BlocksConstants.AuthorizationHeaderName] = "Bearer token-123";

        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = httpContext };

        var context = new TokenValidatedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(identity)
        };

        await events.OnTokenValidated(context);

        Assert.Equal("token-123", identity.FindFirst(BlocksContext.TOKEN_CLAIM)?.Value);
        Assert.Contains("/api/orders", identity.FindFirst(BlocksContext.REQUEST_URI_CLAIM)?.Value);
    }

    [Fact]
    public async Task ConfigureTokenValidationAsync_ShouldSetOptions_WhenCertificateExistsInCache()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
        var method = type!.GetMethod("ConfigureTokenValidationAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var certBytes = cert.Export(X509ContentType.Pfx);

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantTokenValidationParameter("tenant-cert")).Returns(new JwtTokenParameters
        {
            Issuer = "issuer",
            Subject = "subject",
            Audiences = ["aud"],
            PublicCertificatePath = "unused",
            PublicCertificatePassword = string.Empty,
            PrivateCertificatePassword = string.Empty,
            IssueDate = DateTime.UtcNow
        });

        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(certBytes);

        var clientFactory = new Mock<IHttpClientFactory>();
        var context = new MessageReceivedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Token = "x",
        };

        var task = (Task)method!.Invoke(null, [context, tenants.Object, cacheDb.Object, clientFactory.Object, "tenant-cert"])!;
        await task;

        Assert.NotNull(context.Options.TokenValidationParameters);
        Assert.True(context.Options.TokenValidationParameters.ValidateIssuerSigningKey);
    }

    [Fact]
    public async Task GetFromJwksUrl_ShouldLoadSigningKeysAndValidationFlags()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
        var method = type!.GetMethod("GetFromJwksUrl", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var rsa = RSA.Create(2048);
        var rsaKey = new RsaSecurityKey(rsa) { KeyId = "kid-1" };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(rsaKey);
        jwk.Kid = "kid-1";
        var jwksJson = JsonSerializer.Serialize(new { keys = new[] { jwk } });

        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(jwksJson));
        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var tenant = new Blocks.Genesis.Tenant
        {
            TenantId = "tenant-jwks",
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
            ThirdPartyJwtTokenParameters = new ThirdPartyJwtTokenParameters
            {
                JwksUrl = "https://example.local/jwks",
                Issuer = "issuer-jwks",
                Audiences = ["aud1"]
            }
        };

        var task = (Task<TokenValidationParameters>)method!.Invoke(null, [tenant, clientFactory.Object])!;
        var result = await task;

        Assert.True(result.ValidateIssuer);
        Assert.True(result.ValidateAudience);
        Assert.Equal("issuer-jwks", result.ValidIssuer);
        Assert.NotEmpty(result.IssuerSigningKeys);
    }

    [Fact]
    public async Task ValidateTokenWithFallbackAsync_ShouldReturnTrue_ForValidToken()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
        var method = type!.GetMethod("ValidateTokenWithFallbackAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "kid-2" };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: "issuer-fallback",
            audience: "aud-fallback",
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, "user-fallback"),
                new Claim(ClaimTypes.Email, "fallback@example.com"),
                new Claim("oauth", "token-value"),
                new Claim("exp", DateTime.UtcNow.AddMinutes(15).ToString("o")),
                new Claim("realm_access", "{\"roles\":[\"reader\"]}"),
                new Claim("profile", "{\"name\":\"Fallback User\",\"username\":\"fallback.user\",\"email\":\"fallback@example.com\"}")
            ],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "issuer-fallback",
            ValidateAudience = true,
            ValidAudience = "aud-fallback",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        var dbContext = new Mock<IDbContextProvider>();
        var claimsMapperCollection = new Mock<IMongoCollection<BsonDocument>>();
        claimsMapperCollection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(new BsonDocument
            {
                ["Roles"] = "realm_access.roles",
                ["UserId"] = "sub",
                ["Email"] = "profile.email",
                ["UserName"] = "profile.username",
                ["Name"] = "profile.name"
            }));
        dbContext.Setup(d => d.GetCollection<BsonDocument>("ThirdPartyJWTClaims")).Returns(claimsMapperCollection.Object);

        var services = new ServiceCollection();
        services.AddSingleton(dbContext.Object);
        var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Headers[BlocksConstants.BlocksKey] = "tenant-fallback";
        var context = new TokenValidatedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var resultTask = (Task<bool>)method!.Invoke(null, [jwt, validation, context])!;
        var result = await resultTask;

        Assert.True(result);
        Assert.NotNull(context.Principal);
        Assert.True(httpContext.Request.Headers.ContainsKey("ThirdPartyContext"));
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

    [Fact]
    public void JwtBearerAuthentication_ShouldConfigureAuthenticationAndInitializeAccessor()
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
        var method = type!.GetMethod("JwtBearerAuthentication", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var tenants = new Mock<ITenants>();
        var cacheDb = new Mock<IDatabase>();
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.CacheDatabase()).Returns(cacheDb.Object);

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddSingleton(tenants.Object);
        services.AddSingleton(cache.Object);

        method!.Invoke(null, [services]);

        Assert.NotNull(BlocksHttpContextAccessor.Instance);
        var provider = services.BuildServiceProvider();
        var authOptions = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        Assert.NotNull(authOptions.GetPolicy("Protected"));
        Assert.NotNull(authOptions.GetPolicy("Secret"));
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

        var identityWithoutRolesProperty = new ClaimsIdentity([new Claim("realm_access", "{\"other\":[\"x\"]}")]);
        var missingProperty = (string[])method!.Invoke(null, [identityWithoutRolesProperty, mapper])!;
        Assert.Empty(missingProperty);

        var nullIdentity = (string[])method!.Invoke(null, [null!, mapper])!;
        Assert.Empty(nullIdentity);
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
    public async Task OnMessageReceived_ShouldReturnImmediately_WhenTokenIsMissing()
    {
        var tenants = new Mock<ITenants>();
        var cacheDb = new Mock<IDatabase>();
        var clientFactory = new Mock<IHttpClientFactory>();
        var events = BuildJwtEvents(tenants.Object, cacheDb.Object, clientFactory.Object);

        var context = new MessageReceivedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        await events.OnMessageReceived(context);

        Assert.Equal(string.Empty, context.HttpContext.Items["blocks.auth.accessToken"]?.ToString());
        Assert.Null(context.Token);
    }

    [Fact]
    public async Task OnMessageReceived_ShouldExecuteThirdPartyFallbackBranch()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(BlocksContext.Create(
            "tenant-third", [], "", false, "", "", DateTime.MinValue, "", [], "", "", "", "", "", "tenant-third"));

        try
        {
            var tenants = new Mock<ITenants>();
            tenants.Setup(t => t.GetTenantByID("tenant-third")).Returns(new Blocks.Genesis.Tenant
            {
                TenantId = "tenant-third",
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
                ThirdPartyJwtTokenParameters = new ThirdPartyJwtTokenParameters
                {
                    CookieKey = "tp_cookie",
                    JwksUrl = string.Empty,
                    PublicCertificatePath = "missing.cer"
                }
            });

            var cacheDb = new Mock<IDatabase>();
            var clientFactory = new Mock<IHttpClientFactory>();
            var events = BuildJwtEvents(tenants.Object, cacheDb.Object, clientFactory.Object);

            var http = new DefaultHttpContext();
            http.Request.Headers[BlocksConstants.BlocksKey] = "tenant-third";
            http.Request.Headers.Append("Cookie", "tp_cookie=third-party-token");

            var context = new MessageReceivedContext(
                http,
                new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
                new JwtBearerOptions());

            await events.OnMessageReceived(context);

            Assert.Equal("third-party-token", http.Items["blocks.auth.accessToken"]?.ToString());
            Assert.Equal("tenant-third", http.Items["blocks.auth.tenantId"]?.ToString());
            Assert.Null(context.Token);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = false;
        }
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
        var scheme = new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var messageContext = new MessageReceivedContext(httpContext, scheme, options)
        {
            Token = "token-value"
        };

        var tenants = new Mock<ITenants>();
        var cacheDb = new Mock<IDatabase>();
        var clientFactory = new Mock<IHttpClientFactory>();

        var task = (Task)method!.Invoke(null, [messageContext, tenants.Object, cacheDb.Object, clientFactory.Object, null])!;
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
        var scheme = new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var messageContext = new MessageReceivedContext(httpContext, scheme, options)
        {
            Token = "token-value"
        };

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantTokenValidationParameter("tenant-z")).Returns((JwtTokenParameters?)null);

        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var clientFactory = new Mock<IHttpClientFactory>();

        var task = (Task)method!.Invoke(null, [messageContext, tenants.Object, cacheDb.Object, clientFactory.Object, "tenant-z"])!;
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
    public async Task OnAuthenticationFailed_ShouldReturnEarly_ForExpiredToken()
    {
        var tenants = new Mock<ITenants>();
        var cacheDb = new Mock<IDatabase>();
        var clientFactory = new Mock<IHttpClientFactory>();
        var events = BuildJwtEvents(tenants.Object, cacheDb.Object, clientFactory.Object);

        var context = new AuthenticationFailedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Exception = new SecurityTokenExpiredException("expired")
        };

        var ex = await Record.ExceptionAsync(() => events.OnAuthenticationFailed(context));
        Assert.Null(ex);
    }

    [Fact]
    public async Task OnAuthenticationFailed_ShouldAttemptFallback_ForNonExpiredException()
    {
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-auth-failed")).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = "tenant-auth-failed",
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

        var cacheDb = new Mock<IDatabase>();
        var clientFactory = new Mock<IHttpClientFactory>();
        var events = BuildJwtEvents(tenants.Object, cacheDb.Object, clientFactory.Object);

        var context = new AuthenticationFailedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Exception = new InvalidOperationException("boom")
        };
        context.HttpContext.Items["blocks.auth.accessToken"] = "token-x";
        context.HttpContext.Items["blocks.auth.tenantId"] = "tenant-auth-failed";

        var ex = await Record.ExceptionAsync(() => events.OnAuthenticationFailed(context));
        Assert.Null(ex);
    }

    [Fact]
    public async Task OnForbidden_ShouldComplete_WithoutThrowing()
    {
        var tenants = new Mock<ITenants>();
        var cacheDb = new Mock<IDatabase>();
        var clientFactory = new Mock<IHttpClientFactory>();
        var events = BuildJwtEvents(tenants.Object, cacheDb.Object, clientFactory.Object);

        var context = new ForbiddenContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var ex = await Record.ExceptionAsync(() => events.OnForbidden(context));
        Assert.Null(ex);
    }

    [Fact]
    public async Task TryFallbackAsync_ShouldUsePublicCertificatePath_WhenJwksIsNotConfigured()
    {
        var tempCertPath = Path.Combine(Path.GetTempPath(), $"fallback-cert-{Guid.NewGuid():N}.cer");
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=fallback", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(15));
        await File.WriteAllBytesAsync(tempCertPath, cert.Export(X509ContentType.Cert));

        try
        {
            var context = new TokenValidatedContext(
                new DefaultHttpContext(),
                new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
                new JwtBearerOptions());

            var tenants = new Mock<ITenants>();
            tenants.Setup(t => t.GetTenantByID("tenant-fallback-cert")).Returns(new Blocks.Genesis.Tenant
            {
                TenantId = "tenant-fallback-cert",
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
                ThirdPartyJwtTokenParameters = new ThirdPartyJwtTokenParameters
                {
                    Issuer = "issuer-fallback",
                    Audiences = ["aud-fallback"],
                    PublicCertificatePath = tempCertPath,
                    PublicCertificatePassword = string.Empty,
                    JwksUrl = string.Empty
                }
            });

            var result = await InvokeTryFallbackAsync(context, tenants.Object, "not-a-jwt", "tenant-fallback-cert", new Mock<IHttpClientFactory>().Object, null);
            Assert.False(result);
        }
        finally
        {
            if (File.Exists(tempCertPath))
            {
                File.Delete(tempCertPath);
            }
        }
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

    private static JwtBearerEvents BuildJwtEvents(ITenants tenants, IDatabase cacheDb, IHttpClientFactory httpClientFactory)
    {
        var type = Type.GetType("Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis");
        Assert.NotNull(type);
        var method = type!.GetMethod("ConfigureAuthentication", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        method!.Invoke(null, [services, tenants, cacheDb, httpClientFactory]);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(options.Events);
        return options.Events;
    }

    private static IAsyncCursor<BsonDocument> CreateCursor(BsonDocument? document)
    {
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        var sequence = new Queue<List<BsonDocument>>();
        sequence.Enqueue(document is null ? [] : [document]);
        sequence.Enqueue([]);

        cursor.Setup(c => c.MoveNext(It.IsAny<CancellationToken>()))
              .Returns(() =>
              {
                  if (sequence.Count == 0) return false;
                  cursor.SetupGet(x => x.Current).Returns(sequence.Dequeue());
                  return true;
              });

        cursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(() =>
              {
                  if (sequence.Count == 0) return false;
                  cursor.SetupGet(x => x.Current).Returns(sequence.Dequeue());
                  return true;
              });

        return cursor.Object;
    }

    private sealed class StaticResponseHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StaticResponseHttpMessageHandler(string json)
        {
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("network error");
        }
    }
}
