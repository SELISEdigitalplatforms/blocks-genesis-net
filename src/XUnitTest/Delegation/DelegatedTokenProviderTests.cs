using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;

namespace XUnitTest.Delegation;

/// <summary>
/// Cache, single-flight and renewal behaviour of the exchange, plus the signature conformance
/// vector shared with blocks-genesis-py.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class DelegatedTokenProviderTests : IDisposable
{
    private const string TenantId = "tenant-1";
    private const string TenantSalt = "salt-value";
    private const string Endpoint = "http://blocks-iam:8080/api/oidc/token";

    private readonly bool _originalTestMode = BlocksContext.IsTestMode;

    public DelegatedTokenProviderTests()
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

    /// <summary>Counts exchanges and lets a test choose the response per call.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;
        private int _calls;

        public CountingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) => _respond = respond;

        /// <summary>
        /// Awaited before responding, so a test can hold the exchange open. Must be awaited rather
        /// than blocked on: GetTokenAsync runs synchronously up to its first real await, so blocking
        /// here would deadlock any caller not wrapped in Task.Run.
        /// </summary>
        public Func<Task>? Gate { get; set; }

        public int Calls => Volatile.Read(ref _calls);
        public List<Dictionary<string, string>> Forms { get; } = [];
        public List<string?> BlocksKeyHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls);

            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));

                lock (Forms) Forms.Add(form);
            }

            lock (BlocksKeyHeaders)
            {
                BlocksKeyHeaders.Add(request.Headers.TryGetValues(BlocksConstants.BlocksKey, out var values) ? values.First() : null);
            }

            if (Gate is not null)
            {
                await Gate().ConfigureAwait(false);
            }

            return _respond(request, index);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2025, 2, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpResponseMessage TokenResponse(string accessToken, int expiresIn = 300)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"access_token\":\"{accessToken}\",\"token_type\":\"Bearer\",\"expires_in\":{expiresIn}}}",
                Encoding.UTF8,
                "application/json")
        };

    private static (DelegatedTokenProvider Provider, CountingHandler Handler, FixedTimeProvider Clock) CreateProvider(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond,
        string? tenantSalt = TenantSalt)
    {
        var handler = new CountingHandler(respond);
        var clock = new FixedTimeProvider();

        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(DelegationConstants.ExchangeHttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID(TenantId)).Returns(
            tenantSalt is null
                ? null
                : new Blocks.Genesis.Tenant
                {
                    TenantId = TenantId,
                    TenantSalt = tenantSalt,
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
        resolver.Setup(r => r.GetTokenEndpointAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(Endpoint);

        var provider = new DelegatedTokenProvider(
            tenants.Object,
            resolver.Object,
            factory.Object,
            new Mock<ILogger<DelegatedTokenProvider>>().Object,
            activitySource: null,
            timeProvider: clock);

        return (provider, handler, clock);
    }

    [Fact]
    public async Task GetTokenAsync_ShouldReturnNull_WhenNoGrantIsInScope()
    {
        var (provider, handler, _) = CreateProvider((_, _) => TokenResponse("t"));
        DelegatedTokenContext.Clear();

        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetTokenAsync_ShouldReturnNull_WhenContextCarriesNoTenant()
    {
        var (provider, handler, _) = CreateProvider((_, _) => TokenResponse("t"));
        BlocksContext.SetContext(null);
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetTokenAsync_ShouldSendTheRfc8693FormAndTenantHeader()
    {
        var (provider, handler, clock) = CreateProvider((_, _) => TokenResponse("access-1"));
        var grantId = DelegationTestDoubles.SampleGrantId();
        DelegatedTokenContext.Set(grantId);

        Assert.Equal("access-1", await provider.GetTokenAsync());

        var form = handler.Forms.Single();
        Assert.Equal(DelegationConstants.TokenExchangeGrantType, form["grant_type"]);
        Assert.Equal(grantId, form["subject_token"]);
        Assert.Equal(DelegationConstants.DelegationGrantTokenType, form["subject_token_type"]);
        Assert.Equal(clock.Now.ToUnixTimeSeconds().ToString(), form["ts"]);
        Assert.Equal(32, form["nonce"].Length);
        Assert.Equal(64, form["sig"].Length);

        // Signed with the tenant salt over the exact pipe-delimited input.
        var expected = DelegationSignature.Compute(
            TenantId, grantId, form["nonce"], clock.Now.ToUnixTimeSeconds(), TenantSalt);
        Assert.Equal(expected, form["sig"]);

        Assert.Equal(TenantId, handler.BlocksKeyHeaders.Single());
    }

    [Fact]
    public async Task GetTokenAsync_ShouldServeTheCachedToken_InsideValidity()
    {
        var (provider, handler, _) = CreateProvider((_, index) => TokenResponse($"access-{index}"));
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        Assert.Equal("access-1", await provider.GetTokenAsync());
        Assert.Equal("access-1", await provider.GetTokenAsync());
        Assert.Equal("access-1", await provider.GetTokenAsync());

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetTokenAsync_ShouldRefetch_OnceInsideTheRenewalMargin()
    {
        // 300s lifetime, so the entry stops being served at 240s.
        var (provider, handler, clock) = CreateProvider((_, index) => TokenResponse($"access-{index}", expiresIn: 300));
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        Assert.Equal("access-1", await provider.GetTokenAsync());

        clock.Now = clock.Now.AddSeconds(239);
        Assert.Equal("access-1", await provider.GetTokenAsync());
        Assert.Equal(1, handler.Calls);

        clock.Now = clock.Now.AddSeconds(2);
        Assert.Equal("access-2", await provider.GetTokenAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetTokenAsync_ShouldPerformExactlyOneExchange_ForFiftyConcurrentCallers()
    {
        var gate = new TaskCompletionSource();
        var (provider, handler, _) = CreateProvider((_, index) => TokenResponse($"access-{index}"));
        handler.Gate = () => gate.Task;

        var grantId = DelegationTestDoubles.SampleGrantId();
        DelegatedTokenContext.Set(grantId);

        var context = BlocksContext.GetContext();

        // Each task re-establishes the ambient state: AsyncLocal does not flow into Task.Run in
        // the way a fresh worker thread would, and the point of the test is one shared exchange.
        var callers = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            BlocksContext.SetContext(context);
            DelegatedTokenContext.Set(grantId);
            return await provider.GetTokenAsync();
        })).ToArray();

        gate.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.Equal(1, handler.Calls);
        Assert.All(tokens, token => Assert.Equal("access-1", token));
    }

    [Fact]
    public async Task GetTokenAsync_ShouldReturnNull_AndNotCacheAFailure_WhenTheExchangeIsRejected()
    {
        var (provider, handler, _) = CreateProvider((_, index) => index == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
            }
            : TokenResponse("access-recovered"));

        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        Assert.Null(await provider.GetTokenAsync());

        // One rejection costs exactly one round trip: the call does not immediately retry.
        Assert.Equal(1, handler.Calls);

        // The rejection is not cached either, so a later call is free to try again.
        Assert.Equal("access-recovered", await provider.GetTokenAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetTokenAsync_ShouldReturnNull_WhenTheExchangeThrows()
    {
        var (provider, _, _) = CreateProvider((_, _) => throw new HttpRequestException("connection refused"));
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        Assert.Null(await provider.GetTokenAsync());
    }

    [Fact]
    public async Task GetTokenAsync_ShouldReturnNull_WhenTheTenantHasNoSalt()
    {
        var (provider, handler, _) = CreateProvider((_, _) => TokenResponse("t"), tenantSalt: null);
        DelegatedTokenContext.Set(DelegationTestDoubles.SampleGrantId());

        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Invalidate_ShouldDropTheCachedToken()
    {
        var (provider, handler, _) = CreateProvider((_, index) => TokenResponse($"access-{index}"));
        var grantId = DelegationTestDoubles.SampleGrantId();
        DelegatedTokenContext.Set(grantId);

        Assert.Equal("access-1", await provider.GetTokenAsync());

        provider.Invalidate(grantId);

        Assert.Equal("access-2", await provider.GetTokenAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetTokenAsync_ShouldNotCancelTheSharedExchange_WhenOneCallerGivesUp()
    {
        var released = new TaskCompletionSource();
        var (provider, handler, _) = CreateProvider((_, index) => TokenResponse($"access-{index}"));
        handler.Gate = () => released.Task;

        var grantId = DelegationTestDoubles.SampleGrantId();
        DelegatedTokenContext.Set(grantId);

        using var impatient = new CancellationTokenSource();
        var abandoned = provider.GetTokenAsync(impatient.Token);

        // A second caller joins the same in-flight exchange, then the first gives up.
        var patient = Task.Run(async () =>
        {
            BlocksContext.SetContext(BlocksContext.Create(
                tenantId: TenantId, roles: null, userId: "user-1", isAuthenticated: true,
                requestUri: null, organizationId: "org-1", expireOn: DateTime.UtcNow.AddHours(1),
                email: null, permissions: null, userName: null, phoneNumber: null,
                displayName: null, oauthToken: null, originalTenantId: TenantId));
            DelegatedTokenContext.Set(grantId);
            return await provider.GetTokenAsync();
        });

        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        released.SetResult();

        // The exchange survived the cancellation and still served the caller that waited.
        Assert.Equal("access-1", await patient);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Sign_ShouldMatchTheCrossSdkConformanceVector()
    {
        var input = DelegationConstants.BuildSignatureInput(
            DelegationConformanceVector.TenantId,
            DelegationConformanceVector.DelegationId,
            DelegationConformanceVector.Nonce,
            DelegationConformanceVector.Ts);

        Assert.Equal(DelegationConformanceVector.ExpectedSignatureInput, input);
        Assert.Equal(
            DelegationConformanceVector.ExpectedSignature,
            DelegationSignature.Compute(input, DelegationConformanceVector.TenantSalt));
        Assert.True(DelegationSignature.Verify(
            DelegationConformanceVector.ExpectedSignature,
            DelegationSignature.Compute(input, DelegationConformanceVector.TenantSalt)));
        Assert.False(DelegationSignature.Verify(DelegationConformanceVector.ExpectedSignature, "deadbeef"));
    }

    [Fact]
    public void NewNonce_ShouldBeThirtyTwoLowercaseHexChars()
    {
        var nonces = Enumerable.Range(0, 100).Select(_ => DelegationSignature.NewNonce()).ToList();

        Assert.All(nonces, nonce =>
        {
            Assert.Equal(DelegationConstants.NonceRandomBytes * 2, nonce.Length);
            Assert.Matches("^[0-9a-f]+$", nonce);
        });

        Assert.Equal(nonces.Count, nonces.Distinct(StringComparer.Ordinal).Count());
    }
}
