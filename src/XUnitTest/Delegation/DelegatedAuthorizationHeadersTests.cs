using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;

namespace XUnitTest.Delegation;

/// <summary>
/// Delegation is opt-in per call. The caller asks for headers and passes them to
/// <c>IHttpService</c>, so the call is still traced — but nothing is ever attached to a request the
/// caller did not ask about. A worker calling a third party must not hand it a Blocks credential.
/// </summary>
public class DelegatedAuthorizationHeadersTests : IDisposable
{
    private const string TenantId = "tenant-1";
    private const string TenantSalt = "salt-value";

    private readonly bool _originalTestMode = BlocksContext.IsTestMode;

    public DelegatedAuthorizationHeadersTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(BlocksContext.Create(
            tenantId: TenantId, roles: null, userId: "user-1", isAuthenticated: true,
            requestUri: null, organizationId: "org-1", expireOn: DateTime.UtcNow.AddHours(1),
            email: null, permissions: null, userName: null, phoneNumber: null,
            displayName: null, oauthToken: null, originalTenantId: TenantId));
    }

    public void Dispose()
    {
        BlocksContext.SetContext(null);
        DelegatedTokenContext.Clear();
        BlocksContext.IsTestMode = _originalTestMode;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string? _token;

        public StubHandler(string? token) => _token = token;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            if (_token is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"access_token\":\"{_token}\",\"token_type\":\"Bearer\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private static (DelegatedTokenProvider Provider, StubHandler Handler) CreateProvider(string? token)
    {
        var handler = new StubHandler(token);

        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(DelegationConstants.ExchangeHttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID(TenantId)).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = TenantId,
            TenantSalt = TenantSalt,
            DbConnectionString = "mongodb://unit-test",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = [],
                PublicCertificatePath = "none",
                PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty,
                IssueDate = DateTime.UtcNow
            }
        });

        var resolver = new Mock<IDelegationTokenEndpointResolver>();
        resolver
            .Setup(r => r.GetTokenEndpointAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://blocks-iam:8080/api/oidc/token");

        var provider = new DelegatedTokenProvider(
            tenants.Object,
            resolver.Object,
            factory.Object,
            new Mock<ILogger<DelegatedTokenProvider>>().Object);

        return (provider, handler);
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldReturnBearerAndTenantHeader_WhenAGrantIsInScope()
    {
        var (provider, _) = CreateProvider("delegated-token");
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        var headers = await provider.GetAuthorizationHeadersAsync();

        Assert.Equal("Bearer delegated-token", headers[BlocksConstants.AuthorizationHeaderName]);
        Assert.Equal(TenantId, headers[BlocksConstants.BlocksKey]);
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldPreserveTheCallersOtherHeaders()
    {
        var (provider, _) = CreateProvider("delegated-token");
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        var headers = await provider.GetAuthorizationHeadersAsync(new Dictionary<string, string>
        {
            ["Accept"] = "application/json",
            ["X-Correlation-Id"] = "abc"
        });

        Assert.Equal("application/json", headers["Accept"]);
        Assert.Equal("abc", headers["X-Correlation-Id"]);
        Assert.Equal("Bearer delegated-token", headers[BlocksConstants.AuthorizationHeaderName]);
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldNotMutateTheCallersDictionary()
    {
        var (provider, _) = CreateProvider("delegated-token");
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        var callerHeaders = new Dictionary<string, string> { ["Accept"] = "application/json" };

        await provider.GetAuthorizationHeadersAsync(callerHeaders);

        // The caller may reuse this dictionary, so it must come back untouched.
        Assert.Single(callerHeaders);
        Assert.False(callerHeaders.ContainsKey(BlocksConstants.AuthorizationHeaderName));
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldReturnNothingExtra_WhenThereIsNoGrant()
    {
        var (provider, handler) = CreateProvider("delegated-token");
        DelegatedTokenContext.Clear();

        var headers = await provider.GetAuthorizationHeadersAsync();

        Assert.Empty(headers);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldNeverOverrideACallerSuppliedAuthorization()
    {
        var (provider, handler) = CreateProvider("delegated-token");
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        var headers = await provider.GetAuthorizationHeadersAsync(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer caller-supplied"
        });

        Assert.Equal("Bearer caller-supplied", headers[BlocksConstants.AuthorizationHeaderName]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldTreatAuthorizationCaseInsensitively()
    {
        var (provider, handler) = CreateProvider("delegated-token");
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        var headers = await provider.GetAuthorizationHeadersAsync(new Dictionary<string, string>
        {
            ["authorization"] = "Bearer caller-supplied"
        });

        Assert.Equal("Bearer caller-supplied", headers[BlocksConstants.AuthorizationHeaderName]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldNotOverrideACallerSuppliedTenantHeader()
    {
        var (provider, _) = CreateProvider("delegated-token");
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        var headers = await provider.GetAuthorizationHeadersAsync(new Dictionary<string, string>
        {
            [BlocksConstants.BlocksKey] = "explicit-tenant"
        });

        Assert.Equal("explicit-tenant", headers[BlocksConstants.BlocksKey]);
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldAddNothing_WhenTheGrantCannotBeRedeemed()
    {
        var (provider, _) = CreateProvider(token: null);
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        var headers = await provider.GetAuthorizationHeadersAsync(new Dictionary<string, string>
        {
            ["Accept"] = "application/json"
        });

        // The caller's headers survive; no half-formed credential is invented.
        Assert.Single(headers);
        Assert.Equal("application/json", headers["Accept"]);
    }

    [Fact]
    public async Task GetAuthorizationHeadersAsync_ShouldReuseTheCachedToken()
    {
        var (provider, handler) = CreateProvider("delegated-token");
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        await provider.GetAuthorizationHeadersAsync();
        await provider.GetAuthorizationHeadersAsync();
        await provider.GetAuthorizationHeadersAsync();

        Assert.Equal(1, handler.Calls);
    }
}
