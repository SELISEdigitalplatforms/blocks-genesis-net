using Blocks.Genesis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;

namespace XUnitTest.Delegation;

/// <summary>
/// Section 5.5: discovery first, a complete configured URL as fallback, and a hard startup failure
/// when neither is present. The path is never guessed.
/// </summary>
public class DelegationTokenEndpointResolverTests : IDisposable
{
    private const string TenantId = "tenant-1";

    private readonly List<string> _touchedEnvironmentKeys = [];

    public void Dispose()
    {
        foreach (var key in _touchedEnvironmentKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private void SetEnvironment(string key, string? value)
    {
        _touchedEnvironmentKeys.Add(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(_respond(request));
        }
    }

    private static (DelegationTokenEndpointResolver Resolver, StubHandler Handler) CreateResolver(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        IConfiguration? configuration = null)
    {
        var handler = new StubHandler(respond);
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(DelegationConstants.ExchangeHttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var resolver = new DelegationTokenEndpointResolver(
            factory.Object,
            new Mock<ILogger<DelegationTokenEndpointResolver>>().Object,
            configuration);

        return (resolver, handler);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static HttpResponseMessage Discovery(string tokenEndpoint)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"token_endpoint\":\"{tokenEndpoint}\"}}", Encoding.UTF8, "application/json")
        };

    [Fact]
    public void EnsureConfigured_ShouldThrow_WhenNeitherKeyIsSet()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, null);
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, _) = CreateResolver(_ => Discovery("x"), Configuration());

        var exception = Assert.Throws<InvalidOperationException>(resolver.EnsureConfigured);
        Assert.Contains(DelegationConstants.IamBaseUrlKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains(DelegationConstants.IamTokenEndpointKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureConfigured_ShouldPass_WhenOnlyTheFallbackEndpointIsSet()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, null);
        var (resolver, _) = CreateResolver(
            _ => Discovery("x"),
            Configuration((DelegationConstants.IamTokenEndpointKey, "http://blocks-iam:8080/api/oidc/token")));

        resolver.EnsureConfigured();
    }

    [Fact]
    public void EnsureConfigured_ShouldPass_WhenOnlyTheBaseUrlIsSet()
    {
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);
        var (resolver, _) = CreateResolver(
            _ => Discovery("x"),
            Configuration((DelegationConstants.IamBaseUrlKey, "http://blocks-iam:8080")));

        resolver.EnsureConfigured();
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldUseDiscovery_AndQueryThePerTenantDocument()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, "http://blocks-iam:8080/");
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, handler) = CreateResolver(_ => Discovery("http://blocks-iam:8080/api/oidc/token?tenant_id=tenant-1"));

        var endpoint = await resolver.GetTokenEndpointAsync(TenantId);

        Assert.Equal("http://blocks-iam:8080/api/oidc/token?tenant_id=tenant-1", endpoint);
        Assert.Equal($"http://blocks-iam:8080/{TenantId}/.well-known/openid-configuration", handler.RequestedUrls.Single());
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldCacheASuccessfulDiscovery()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, "http://blocks-iam:8080");
        var (resolver, handler) = CreateResolver(_ => Discovery("http://blocks-iam:8080/api/oidc/token"));

        await resolver.GetTokenEndpointAsync(TenantId);
        await resolver.GetTokenEndpointAsync(TenantId);
        await resolver.GetTokenEndpointAsync(TenantId);

        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldFallBackToTheConfiguredUrl_WhenDiscoveryIsUnreachable()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, "http://blocks-iam:8080");
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, _) = CreateResolver(
            _ => throw new HttpRequestException("connection refused"),
            Configuration((DelegationConstants.IamTokenEndpointKey, "http://blocks-iam:8080/api/oidc/token")));

        Assert.Equal("http://blocks-iam:8080/api/oidc/token", await resolver.GetTokenEndpointAsync(TenantId));
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldRetryDiscoveryLazily_AfterUsingTheFallback()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, "http://blocks-iam:8080");
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var attempts = 0;
        var (resolver, _) = CreateResolver(
            _ =>
            {
                attempts++;
                if (attempts == 1) throw new HttpRequestException("boot: IAM not up yet");
                return Discovery("http://blocks-iam:8080/api/oidc/token");
            },
            Configuration((DelegationConstants.IamTokenEndpointKey, "http://fallback:8080/api/oidc/token")));

        Assert.Equal("http://fallback:8080/api/oidc/token", await resolver.GetTokenEndpointAsync(TenantId));
        Assert.Equal("http://blocks-iam:8080/api/oidc/token", await resolver.GetTokenEndpointAsync(TenantId));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldFallBack_WhenDiscoveryHasNoTokenEndpoint()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, "http://blocks-iam:8080");
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, _) = CreateResolver(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"issuer\":\"http://blocks-iam:8080\"}", Encoding.UTF8, "application/json")
            },
            Configuration((DelegationConstants.IamTokenEndpointKey, "http://blocks-iam:8080/api/oidc/token")));

        Assert.Equal("http://blocks-iam:8080/api/oidc/token", await resolver.GetTokenEndpointAsync(TenantId));
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldFallBack_WhenDiscoveryReturnsAnErrorStatus()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, "http://blocks-iam:8080");
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, _) = CreateResolver(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            Configuration((DelegationConstants.IamTokenEndpointKey, "http://blocks-iam:8080/api/oidc/token")));

        Assert.Equal("http://blocks-iam:8080/api/oidc/token", await resolver.GetTokenEndpointAsync(TenantId));
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldThrow_WhenDiscoveryFailsAndThereIsNoFallback()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, "http://blocks-iam:8080");
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, _) = CreateResolver(_ => throw new HttpRequestException("down"), Configuration());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.GetTokenEndpointAsync(TenantId));
        Assert.Contains("Refusing to guess", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldThrow_WithoutATenant()
    {
        var (resolver, _) = CreateResolver(_ => Discovery("x"), Configuration());

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.GetTokenEndpointAsync(string.Empty));
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldReadTheFrontendRuntimeSection()
    {
        // FrontendRuntime is where Blocks services put runtime settings, so this is the expected home.
        SetEnvironment(DelegationConstants.IamBaseUrlKey, null);
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, handler) = CreateResolver(
            _ => Discovery("http://blocks-iam:8080/api/oidc/token"),
            Configuration(($"{DelegationConstants.FrontendRuntimeSection}:{DelegationConstants.IamBaseUrlKey}", "http://blocks-iam:8080")));

        Assert.Equal("http://blocks-iam:8080/api/oidc/token", await resolver.GetTokenEndpointAsync(TenantId));
        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldStillReadABareRootLevelKey()
    {
        // An appsettings.json that sets the key at the root, outside any section, still resolves.
        SetEnvironment(DelegationConstants.IamBaseUrlKey, null);
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, handler) = CreateResolver(
            _ => Discovery("http://from-root:8080/api/oidc/token"),
            Configuration((DelegationConstants.IamBaseUrlKey, "http://from-root:8080")));

        Assert.Equal("http://from-root:8080/api/oidc/token", await resolver.GetTokenEndpointAsync(TenantId));
        Assert.StartsWith("http://from-root:8080/", handler.RequestedUrls.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldPreferFrontendRuntimeOverTheConfigurationRoot()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, null);
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, handler) = CreateResolver(
            _ => Discovery("http://from-section:8080/api/oidc/token"),
            Configuration(
                (DelegationConstants.IamBaseUrlKey, "http://from-root:8080"),
                ($"{DelegationConstants.FrontendRuntimeSection}:{DelegationConstants.IamBaseUrlKey}", "http://from-section:8080")));

        await resolver.GetTokenEndpointAsync(TenantId);

        Assert.StartsWith("http://from-section:8080/", handler.RequestedUrls.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureConfigured_ShouldPass_WhenOnlyTheFrontendRuntimeSectionIsSet()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, null);
        SetEnvironment(DelegationConstants.IamTokenEndpointKey, null);

        var (resolver, _) = CreateResolver(
            _ => Discovery("x"),
            Configuration(($"{DelegationConstants.FrontendRuntimeSection}:{DelegationConstants.IamTokenEndpointKey}", "http://blocks-iam:8080/api/oidc/token")));

        resolver.EnsureConfigured();
    }

    [Fact]
    public async Task GetTokenEndpointAsync_ShouldPreferTheEnvironmentVariableOverConfiguration()
    {
        SetEnvironment(DelegationConstants.IamBaseUrlKey, "http://from-env:8080");

        var (resolver, handler) = CreateResolver(
            _ => Discovery("http://from-env:8080/api/oidc/token"),
            Configuration(
                (DelegationConstants.IamBaseUrlKey, "http://from-config:8080"),
                ($"{DelegationConstants.FrontendRuntimeSection}:{DelegationConstants.IamBaseUrlKey}", "http://from-section:8080")));

        await resolver.GetTokenEndpointAsync(TenantId);

        Assert.StartsWith("http://from-env:8080/", handler.RequestedUrls.Single(), StringComparison.Ordinal);
    }
}
