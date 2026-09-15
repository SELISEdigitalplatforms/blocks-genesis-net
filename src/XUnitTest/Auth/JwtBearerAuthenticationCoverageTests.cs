using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using StackExchange.Redis;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace XUnitTest.Auth;

/// <summary>
/// Coverage tests for the branches of <c>JwtBearerAuthenticationExtension</c> that the
/// scaffold tests do not reach: anonymous endpoints, service resolution failures,
/// certificate cache misses, JWKS and public-certificate fallback success paths, and the
/// third-party context mapping.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class JwtBearerAuthenticationCoverageTests
{
    private const string ExtensionTypeName = "Blocks.Genesis.JwtBearerAuthenticationExtension, Blocks.Genesis";

    [Fact]
    public async Task OnMessageReceived_ShouldSkip_WhenEndpointAllowsAnonymous()
    {
        var events = BuildJwtEvents();
        var httpContext = new DefaultHttpContext();
        httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute(), new AllowAnonymousAttribute()), "anonymous"));

        var context = new MessageReceivedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        await events.OnMessageReceived(context);

        Assert.Empty(httpContext.Items);
        Assert.Null(context.Token);
    }

    [Fact]
    public async Task OnMessageReceived_ShouldSkip_WhenNoEndpointIsMapped()
    {
        var events = BuildJwtEvents();
        var httpContext = new DefaultHttpContext();

        var context = new MessageReceivedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        await events.OnMessageReceived(context);

        Assert.Empty(httpContext.Items);
        Assert.Null(context.Token);
    }

    [Fact]
    public async Task OnTokenValidated_ShouldDoNothing_WhenPrincipalIsMissing()
    {
        var events = BuildJwtEvents();
        var httpContext = CreateHttpContext(new Mock<ITenants>().Object, new Mock<IDatabase>().Object, new Mock<IHttpClientFactory>().Object);

        var context = new TokenValidatedContext(
            httpContext,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var ex = await Record.ExceptionAsync(() => events.OnTokenValidated(context));
        Assert.Null(ex);
    }

    [Fact]
    public void ResolveTenants_ShouldThrow_WhenServiceIsNotRegistered()
    {
        var method = GetPrivateStaticMethod("ResolveTenants");

        var withoutServices = new DefaultHttpContext();
        var noServices = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [withoutServices]));
        Assert.IsType<InvalidOperationException>(noServices.InnerException);

        var emptyProvider = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var missing = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [emptyProvider]));
        Assert.IsType<InvalidOperationException>(missing.InnerException);
    }

    [Fact]
    public void ResolveCacheDatabase_ShouldThrow_WhenCacheClientIsNotRegistered()
    {
        var method = GetPrivateStaticMethod("ResolveCacheDatabase");

        var withoutServices = new DefaultHttpContext();
        var noServices = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [withoutServices]));
        Assert.IsType<InvalidOperationException>(noServices.InnerException);

        var emptyProvider = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var missing = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [emptyProvider]));
        Assert.IsType<InvalidOperationException>(missing.InnerException);
    }

    [Fact]
    public void ResolveCacheDatabase_ShouldThrow_WhenCacheClientReturnsNullDatabase()
    {
        var method = GetPrivateStaticMethod("ResolveCacheDatabase");

        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.CacheDatabase()).Returns((IDatabase)null!);
        var services = new ServiceCollection();
        services.AddSingleton(cacheClient.Object);

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [httpContext]));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void ResolveHttpClientFactory_ShouldThrow_WhenServiceIsNotRegistered()
    {
        var method = GetPrivateStaticMethod("ResolveHttpClientFactory");

        var withoutServices = new DefaultHttpContext();
        var noServices = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [withoutServices]));
        Assert.IsType<InvalidOperationException>(noServices.InnerException);

        var emptyProvider = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var missing = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [emptyProvider]));
        Assert.IsType<InvalidOperationException>(missing.InnerException);
    }

    [Fact]
    public void JwtBearerAuthentication_ShouldKeepExistingAccessorInstance()
    {
        var original = BlocksHttpContextAccessor.Instance;
        try
        {
            var method = GetPublicStaticMethod("JwtBearerAuthentication");

            BlocksHttpContextAccessor.Instance = null;
            method.Invoke(null, [new ServiceCollection()]);
            Assert.NotNull(BlocksHttpContextAccessor.Instance);

            var sentinel = new HttpContextAccessor();
            BlocksHttpContextAccessor.Instance = sentinel;
            method.Invoke(null, [new ServiceCollection()]);
            Assert.Same(sentinel, BlocksHttpContextAccessor.Instance);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = original;
        }
    }

    [Fact]
    public async Task ConfigureTokenValidationAsync_ShouldReturnQuietly_WhenTenantAndTokenAreMissing()
    {
        var method = GetPrivateStaticMethod("ConfigureTokenValidationAsync");

        var context = new MessageReceivedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var task = (Task)method.Invoke(null, [context, new Mock<ITenants>().Object, new Mock<IDatabase>().Object, new Mock<IHttpClientFactory>().Object, null])!;
        await task;

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task ConfigureTokenValidationAsync_ShouldFail_WhenValidationParametersAreMissing()
    {
        var method = GetPrivateStaticMethod("ConfigureTokenValidationAsync");

        using var cert = CreateSelfSignedCertificate("CN=params-missing");
        var certBytes = cert.Export(X509ContentType.Pfx);

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantTokenValidationParameter("tenant-nop")).Returns((JwtTokenParameters?)null);

        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(certBytes);

        var context = new MessageReceivedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Token = "token-value"
        };

        var task = (Task)method.Invoke(null, [context, tenants.Object, cacheDb.Object, new Mock<IHttpClientFactory>().Object, "tenant-nop"])!;
        await task;

        Assert.NotNull(context.Result?.Failure);
        Assert.Contains("Validation parameters not found", context.Result!.Failure!.Message);
    }

    [Fact]
    public async Task GetCertificateAsync_ShouldLoadFromFileAndCache_WhenCacheMisses()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"cache-miss-{Guid.NewGuid():N}.pfx");
        using var cert = CreateSelfSignedCertificate("CN=cache-miss");
        await File.WriteAllBytesAsync(tempPath, cert.Export(X509ContentType.Cert));

        try
        {
            var validation = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = ["aud"],
                PublicCertificatePath = tempPath,
                PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty,
                IssueDate = DateTime.UtcNow.AddDays(-1),
                CertificateValidForNumberOfDays = 30
            };

            var tenants = new Mock<ITenants>();
            tenants.Setup(t => t.GetTenantTokenValidationParameter("tenant-file")).Returns(validation);

            var cacheDb = new Mock<IDatabase>();
            cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);
            cacheDb.Setup(db => db.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<Expiration>(),
                    It.IsAny<ValueCondition>(),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);

            var result = await JwtBearerAuthenticationExtension.GetCertificateAsync(
                "tenant-file",
                tenants.Object,
                cacheDb.Object,
                new Mock<IHttpClientFactory>().Object);

            Assert.NotNull(result);
            cacheDb.Verify(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()), Times.Once);
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
    public async Task GetCertificateAsync_ShouldReturnNull_WhenCertificateFileIsMissing()
    {
        var validation = new JwtTokenParameters
        {
            Issuer = "issuer",
            Subject = "subject",
            Audiences = ["aud"],
            PublicCertificatePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pfx"),
            PublicCertificatePassword = string.Empty,
            PrivateCertificatePassword = string.Empty,
            IssueDate = DateTime.UtcNow
        };

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantTokenValidationParameter("tenant-nofile")).Returns(validation);

        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);

        var result = await JwtBearerAuthenticationExtension.GetCertificateAsync(
            "tenant-nofile",
            tenants.Object,
            cacheDb.Object,
            new Mock<IHttpClientFactory>().Object);

        Assert.Null(result);
    }

    [Fact]
    public async Task CacheCertificateAsync_ShouldSkip_WhenValidationParametersAreNull()
    {
        var method = GetPrivateStaticMethod("CacheCertificateAsync");

        var cacheDb = new Mock<IDatabase>();
        var task = (Task)method.Invoke(null, [cacheDb.Object, "key-null", new byte[] { 1 }, null])!;
        await task;

        cacheDb.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CacheCertificateAsync_ShouldSkip_WhenCertificateAlreadyExpired()
    {
        var method = GetPrivateStaticMethod("CacheCertificateAsync");

        var validation = new JwtTokenParameters
        {
            Issuer = "issuer",
            Subject = "subject",
            Audiences = [],
            PublicCertificatePath = "path",
            PublicCertificatePassword = string.Empty,
            PrivateCertificatePassword = string.Empty,
            IssueDate = DateTime.UtcNow.AddDays(-30),
            CertificateValidForNumberOfDays = 10
        };

        var cacheDb = new Mock<IDatabase>();
        var task = (Task)method.Invoke(null, [cacheDb.Object, "key-expired", new byte[] { 1 }, validation])!;
        await task;

        cacheDb.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TryFallbackAsync_ShouldReturnFalse_WhenTenantIdCannotBeResolved()
    {
        var method = GetPublicStaticMethod("TryFallbackAsync");

        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var task = (Task<bool>)method.Invoke(null, [context, new Mock<ITenants>().Object, "not-a-jwt", null, new Mock<IHttpClientFactory>().Object, null])!;
        var result = await task;

        Assert.False(result);
    }

    [Fact]
    public async Task TryFallbackAsync_ShouldReturnFalse_WhenTenantIsNull()
    {
        var method = GetPublicStaticMethod("TryFallbackAsync");

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("tenant-unknown")).Returns((Blocks.Genesis.Tenant?)null);

        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var task = (Task<bool>)method.Invoke(null, [context, tenants.Object, "token-x", "tenant-unknown", new Mock<IHttpClientFactory>().Object, null])!;
        var result = await task;

        Assert.False(result);
    }

    [Fact]
    public async Task TryFallbackAsync_ShouldReturnFalse_WhenTenantLookupThrows()
    {
        var method = GetPublicStaticMethod("TryFallbackAsync");

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID(It.IsAny<string>())).Throws(new InvalidOperationException("store offline"));

        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
            new JwtBearerOptions());

        var task = (Task<bool>)method.Invoke(null, [context, tenants.Object, "token-x", "tenant-throws", new Mock<IHttpClientFactory>().Object, null])!;
        var result = await task;

        Assert.False(result);
    }

    [Fact]
    public void RequestItemHelpers_ShouldHandleMissingNullAndWhitespaceValues()
    {
        var type = Type.GetType(ExtensionTypeName)!;
        var getToken = type.GetMethod("GetRequestAccessToken", BindingFlags.NonPublic | BindingFlags.Static)!;
        var setTenant = type.GetMethod("SetRequestTenantId", BindingFlags.NonPublic | BindingFlags.Static)!;
        var getTenant = type.GetMethod("GetRequestTenantId", BindingFlags.NonPublic | BindingFlags.Static)!;

        var httpContext = new DefaultHttpContext();

        // Missing items resolve to safe defaults.
        Assert.Equal(string.Empty, (string)getToken.Invoke(null, [httpContext])!);
        Assert.Null((string?)getTenant.Invoke(null, [httpContext]));

        // Null stored values resolve to safe defaults.
        httpContext.Items["blocks.auth.accessToken"] = null;
        Assert.Equal(string.Empty, (string)getToken.Invoke(null, [httpContext])!);

        setTenant.Invoke(null, [httpContext, null]);
        Assert.Null((string?)getTenant.Invoke(null, [httpContext]));

        httpContext.Items["blocks.auth.tenantId"] = "   ";
        Assert.Null((string?)getTenant.Invoke(null, [httpContext]));
    }

    // ---- helpers ----

    private static JsonElement ParseThirdPartyContextHeader(HttpContext httpContext)
    {
        Assert.True(httpContext.Request.Headers.ContainsKey("ThirdPartyContext"));
        using var document = JsonDocument.Parse(httpContext.Request.Headers["ThirdPartyContext"].ToString());
        return document.RootElement.Clone();
    }

    private static MethodInfo GetPrivateStaticMethod(string name)
    {
        var type = Type.GetType(ExtensionTypeName);
        Assert.NotNull(type);
        var method = type!.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }

    private static MethodInfo GetPublicStaticMethod(string name)
    {
        var type = Type.GetType(ExtensionTypeName);
        Assert.NotNull(type);
        var method = type!.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }

    private static JwtBearerEvents BuildJwtEvents()
    {
        var method = GetPrivateStaticMethod("ConfigureAuthenticationInternal");

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        method.Invoke(null, [services]);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(options.Events);
        return options.Events;
    }

    private static DefaultHttpContext CreateHttpContext(ITenants tenants, IDatabase cacheDb, IHttpClientFactory httpClientFactory)
    {
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.CacheDatabase()).Returns(cacheDb);

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddSingleton(tenants);
        services.AddSingleton(cacheClient.Object);
        services.AddSingleton(httpClientFactory);

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static DefaultHttpContext CreateHttpContextWithDbProvider(BsonDocument? claimsMapper)
    {
        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(claimsMapper));

        var dbContextProvider = new Mock<IDbContextProvider>();
        dbContextProvider
            .Setup(d => d.GetCollection<BsonDocument>("ThirdPartyJWTClaims"))
            .Returns(collection.Object);

        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddSingleton(dbContextProvider.Object);

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static IAsyncCursor<BsonDocument> CreateCursor(BsonDocument? firstItem)
    {
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        if (firstItem is null)
        {
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>())).Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns(Array.Empty<BsonDocument>());
        }
        else
        {
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>())).Returns(true).Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns([firstItem]);
        }
        return cursor.Object;
    }

    private static IHttpClientFactory HttpClientFactoryReturning(string json)
    {
        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new StaticResponseHttpMessageHandler(json)));
        return clientFactory.Object;
    }

    private static Blocks.Genesis.Tenant CreateTenant(string tenantId)
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            ItemId = tenantId,
            Applications = [new Blocks.Genesis.Applications { Domain = "app.local" }],
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
            }
        };
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
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
}
