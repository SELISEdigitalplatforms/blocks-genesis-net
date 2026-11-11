using Blocks.Genesis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XUnitTest.Auth;

#pragma warning disable CA1707, CA2007 // same test naming and await style as the Auth tests next to this file

/// <summary>
/// blocks-genesis-net#131: a Redis failure on the tenant certificate cache is a cache miss, backed by a
/// per-process fallback cache (5 minutes, same cacheability rule as Redis) that is used only after a
/// Redis failure, with one shared download per tenant while Redis reads fail.
/// </summary>
public class JwtCertificateRedisFallbackTests
{
    private const string CertificateUrl = "https://certificates.test/tenant.cer";

    public static TheoryData<Exception> RedisFailures() => new()
    {
        new RedisConnectionException(ConnectionFailureType.UnableToConnect, "No connection is active/available to service this operation: GET tetocertpublic::tenant"),
        new RedisTimeoutException("Timeout awaiting response: GET tetocertpublic::tenant", CommandStatus.Sent),
        new RedisServerException("READONLY You can't write against a read only replica."),
    };

    // H3, H4, H6, C1, C7; Q2 A: no Redis write after a failed read.
    [Theory]
    [MemberData(nameof(RedisFailures))]
    public async Task GetCertificateAsync_ShouldLoadFromPath_WhenRedisReadFails(Exception failure)
    {
        using var fixture = new CertificateFixture("CN=read-failure");
        var cacheDb = RedisReadThrows(failure);

        var result = await GetAsync(fixture, cacheDb, NewCache());

        Assert.NotNull(result);
        Assert.Equal(fixture.Thumbprint, result!.Thumbprint);
        Assert.Equal(1, fixture.Downloads);
        cacheDb.Verify(db => db.StringGetAsync(fixture.CacheKey, It.IsAny<CommandFlags>()), Times.Once);
        VerifyNoRedisWrite(cacheDb);
    }

    // N5: the second request during the outage comes from memory, with no second download; each request
    // still makes its single Redis read (A1, C6: no retries, nothing skipped).
    [Fact]
    public async Task GetCertificateAsync_ShouldServeSecondRequestFromMemory_WhileRedisReadsFail()
    {
        using var fixture = new CertificateFixture("CN=second-request");
        var cacheDb = RedisReadThrows(new RedisConnectionException(ConnectionFailureType.SocketFailure, "socket failure"));
        var cache = NewCache();

        var first = await GetAsync(fixture, cacheDb, cache);
        var second = await GetAsync(fixture, cacheDb, cache);

        Assert.Equal(fixture.Thumbprint, first!.Thumbprint);
        Assert.Equal(fixture.Thumbprint, second!.Thumbprint);
        Assert.Equal(1, fixture.Downloads);
        cacheDb.Verify(db => db.StringGetAsync(fixture.CacheKey, It.IsAny<CommandFlags>()), Times.Exactly(2));
        VerifyNoRedisWrite(cacheDb);
    }

    // N2: the memory entry lives 5 minutes.
    [Fact]
    public async Task GetCertificateAsync_ShouldDownloadAgain_OnlyAfterTheMemoryEntryExpires()
    {
        using var fixture = new CertificateFixture("CN=ttl");
        var cacheDb = RedisReadThrows(new RedisTimeoutException("timeout", CommandStatus.Sent));
        var clock = new ManualTimeProvider();
        var cache = new JwtBearerAuthenticationExtension.CertificateFallbackCache(clock);

        await GetAsync(fixture, cacheDb, cache);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await GetAsync(fixture, cacheDb, cache);
        Assert.Equal(1, fixture.Downloads);

        clock.Advance(TimeSpan.FromSeconds(1));
        var afterExpiry = await GetAsync(fixture, cacheDb, cache);

        Assert.Equal(fixture.Thumbprint, afterExpiry!.Thumbprint);
        Assert.Equal(2, fixture.Downloads);

        await GetAsync(fixture, cacheDb, cache);
        Assert.Equal(2, fixture.Downloads);
    }

    public static TheoryData<int, int> NotCacheableLifetimes() => new()
    {
        { 0, 1 },   // no validity configured
        { 10, 30 }, // validity already used up
    };

    // N2, decided: a certificate Redis would not cache is not kept in memory either.
    [Theory]
    [MemberData(nameof(NotCacheableLifetimes))]
    public async Task GetCertificateAsync_ShouldNotKeepInMemory_WhenRedisRuleWouldNotCache(int validForDays, int issuedDaysAgo)
    {
        using var fixture = new CertificateFixture("CN=not-cacheable", validForDays, issuedDaysAgo);
        var cacheDb = RedisReadThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var cache = NewCache();

        Assert.NotNull(await GetAsync(fixture, cacheDb, cache));
        Assert.NotNull(await GetAsync(fixture, cacheDb, cache));

        Assert.Equal(2, fixture.Downloads);
    }

    // Q1 A: concurrent requests for one tenant share one download while Redis reads fail. The tenant is
    // not cacheable, so only the shared download (not memory) can keep the count at one. Each call joins
    // synchronously: the failed read completes at once and the first await is on the shared download.
    [Fact]
    public async Task GetCertificateAsync_ShouldShareOneDownload_ForConcurrentRequestsDuringOutage()
    {
        using var fixture = new CertificateFixture("CN=single-flight", validForDays: 0) { HoldDownloads = true };
        var cacheDb = RedisReadThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var cache = NewCache();

        var requests = Enumerable.Range(0, 10).Select(_ => GetAsync(fixture, cacheDb, cache)).ToList();
        Assert.All(requests, r => Assert.False(r.IsCompleted));

        fixture.ReleaseDownloads();
        var results = await Task.WhenAll(requests);

        Assert.All(results, r => Assert.Equal(fixture.Thumbprint, r!.Thumbprint));
        Assert.Equal(1, fixture.Downloads);

        // The shared download is over, so a later request starts its own.
        Assert.NotNull(await GetAsync(fixture, cacheDb, cache));
        Assert.Equal(2, fixture.Downloads);
    }

    // Q1 A: when the shared download fails, every request waiting on it fails, as each would today.
    [Fact]
    public async Task GetCertificateAsync_ShouldFailAllWaitingRequests_WhenTheSharedDownloadFails()
    {
        using var fixture = new CertificateFixture("CN=shared-failure") { HoldDownloads = true, DownloadStatus = HttpStatusCode.NotFound };
        var cacheDb = RedisReadThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var cache = NewCache();

        var requests = Enumerable.Range(0, 5).Select(_ => GetAsync(fixture, cacheDb, cache)).ToList();
        fixture.ReleaseDownloads();
        var results = await Task.WhenAll(requests);

        Assert.All(results, r => Assert.Null(r));
        Assert.Equal(1, fixture.Downloads);
    }

    // C4: many parallel requests in an outage all succeed, with one download per tenant.
    [Fact]
    public async Task GetCertificateAsync_ShouldServeParallelRequestsWithOneDownload_DuringOutage()
    {
        using var fixture = new CertificateFixture("CN=parallel");
        var cacheDb = RedisReadThrows(new RedisTimeoutException("timeout", CommandStatus.Sent));
        var cache = NewCache();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => GetAsync(fixture, cacheDb, cache))));

        Assert.All(results, r => Assert.Equal(fixture.Thumbprint, r!.Thumbprint));
        Assert.Equal(1, fixture.Downloads);
    }

    // H5, H7, N5: a failed write does not fail the request; the certificate goes to memory. Q3 B: the
    // next request's read succeeds but misses, and memory serves it without a download or a write.
    [Fact]
    public async Task GetCertificateAsync_ShouldReturnCertificateAndUseMemory_WhenRedisWriteFails()
    {
        using var fixture = new CertificateFixture("CN=write-failure");
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);
        SetupRedisWrite(cacheDb).ThrowsAsync(new RedisServerException("OOM command not allowed when used memory > 'maxmemory'."));
        var cache = NewCache();

        var first = await GetAsync(fixture, cacheDb, cache);
        var second = await GetAsync(fixture, cacheDb, cache);

        Assert.Equal(fixture.Thumbprint, first!.Thumbprint);
        Assert.Equal(fixture.Thumbprint, second!.Thumbprint);
        Assert.Equal(1, fixture.Downloads);
        VerifyRedisWrite(cacheDb, Times.Once());
    }

    // N2: on a plain miss, a certificate the Redis rule would not cache is neither written nor kept in memory.
    [Theory]
    [MemberData(nameof(NotCacheableLifetimes))]
    public async Task GetCertificateAsync_ShouldNeitherWriteNorKeepInMemory_WhenReadMissesForNonCacheableCertificate(int validForDays, int issuedDaysAgo)
    {
        using var fixture = new CertificateFixture("CN=miss-not-cacheable", validForDays, issuedDaysAgo);
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);
        SetupRedisWrite(cacheDb).ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var cache = NewCache();

        Assert.NotNull(await GetAsync(fixture, cacheDb, cache));
        Assert.NotNull(await GetAsync(fixture, cacheDb, cache));

        Assert.Equal(2, fixture.Downloads);
        Assert.False(cache.TryGet(fixture.CacheKey, out _));
        VerifyRedisWrite(cacheDb, Times.Never());
    }

    // N3: while Redis fully works, memory stays empty and every miss downloads and writes as before.
    [Fact]
    public async Task GetCertificateAsync_ShouldNotUseMemory_WhileRedisWorks()
    {
        using var fixture = new CertificateFixture("CN=healthy");
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);
        SetupRedisWrite(cacheDb).ReturnsAsync(true);
        var cache = NewCache();

        await GetAsync(fixture, cacheDb, cache);
        await GetAsync(fixture, cacheDb, cache);

        Assert.Equal(2, fixture.Downloads);
        VerifyRedisWrite(cacheDb, Times.Exactly(2));
        Assert.False(cache.TryGet(fixture.CacheKey, out _));
    }

    // H1, C5: once Redis answers again, its entry wins over the memory entry from the outage.
    [Fact]
    public async Task GetCertificateAsync_ShouldUseRedisHit_AfterRedisRecovers()
    {
        using var outageCertificate = new CertificateFixture("CN=outage");
        using var redisCertificate = CreateCertificate("CN=redis");
        var cacheDb = new Mock<IDatabase>();
        cacheDb.SetupSequence(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"))
            .ReturnsAsync(redisCertificate.Export(X509ContentType.Cert));
        var cache = NewCache();

        var duringOutage = await GetAsync(outageCertificate, cacheDb, cache);
        var afterRecovery = await GetAsync(outageCertificate, cacheDb, cache);

        Assert.Equal(outageCertificate.Thumbprint, duringOutage!.Thumbprint);
        Assert.Equal(redisCertificate.Thumbprint, afterRecovery!.Thumbprint);
        Assert.Equal(1, outageCertificate.Downloads);
    }

    // C3: memory entries are per tenant.
    [Fact]
    public async Task GetCertificateAsync_ShouldKeepTenantsApart_DuringOutage()
    {
        using var tenantA = new CertificateFixture("CN=tenant-a");
        using var tenantB = new CertificateFixture("CN=tenant-b");
        var cacheDb = RedisReadThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var cache = NewCache();

        for (var round = 0; round < 2; round++)
        {
            Assert.Equal(tenantA.Thumbprint, (await GetAsync(tenantA, cacheDb, cache))!.Thumbprint);
            Assert.Equal(tenantB.Thumbprint, (await GetAsync(tenantB, cacheDb, cache))!.Thumbprint);
        }

        Assert.NotEqual(tenantA.Thumbprint, tenantB.Thumbprint);
        Assert.Equal(1, tenantA.Downloads);
        Assert.Equal(1, tenantB.Downloads);
    }

    // H8, C2: Redis down and the certificate unavailable gives null (auth fails), not an exception.
    [Fact]
    public async Task GetCertificateAsync_ShouldReturnNull_WhenRedisFailsAndPathIsUnavailable()
    {
        using var fixture = new CertificateFixture("CN=unavailable") { DownloadStatus = HttpStatusCode.NotFound };
        var cacheDb = RedisReadThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var result = await GetAsync(fixture, cacheDb, NewCache());

        Assert.Null(result);
        VerifyNoRedisWrite(cacheDb);
    }

    [Fact]
    public async Task GetCertificateAsync_ShouldReturnNull_WhenRedisFailsAndNoPathIsConfigured()
    {
        var cacheDb = RedisReadThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantTokenValidationParameter(It.IsAny<string>())).Returns((JwtTokenParameters?)null);

        var result = await JwtBearerAuthenticationExtension.GetCertificateAsync(
            $"tenant-{Guid.NewGuid():N}", tenants.Object, cacheDb.Object, new Mock<IHttpClientFactory>().Object, NewCache());

        Assert.Null(result);
    }

    // Decided: only Redis faults count; any other exception propagates as before.
    [Fact]
    public async Task GetCertificateAsync_ShouldPropagate_NonRedisExceptions()
    {
        using var fixture = new CertificateFixture("CN=other-fault");
        var cacheDb = RedisReadThrows(new InvalidOperationException("not a Redis fault"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsync(fixture, cacheDb, NewCache()));
        Assert.Equal(0, fixture.Downloads);
    }

    // The public signature is unchanged and uses the process-wide fallback cache.
    [Fact]
    public async Task PublicGetCertificateAsync_ShouldFallBackToPath_WhenRedisReadFails()
    {
        using var fixture = new CertificateFixture("CN=public-overload");
        var cacheDb = RedisReadThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var result = await JwtBearerAuthenticationExtension.GetCertificateAsync(
            fixture.TenantId, fixture.Tenants, cacheDb.Object, fixture.HttpClientFactory);

        Assert.Equal(fixture.Thumbprint, result!.Thumbprint);
    }

    private static JwtBearerAuthenticationExtension.CertificateFallbackCache NewCache() => new(TimeProvider.System);

    private static Task<X509Certificate2?> GetAsync(
        CertificateFixture fixture,
        Mock<IDatabase> cacheDb,
        JwtBearerAuthenticationExtension.CertificateFallbackCache cache)
        => JwtBearerAuthenticationExtension.GetCertificateAsync(
            fixture.TenantId, fixture.Tenants, cacheDb.Object, fixture.HttpClientFactory, cache);

    private static Mock<IDatabase> RedisReadThrows(Exception failure)
    {
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ThrowsAsync(failure);
        return cacheDb;
    }

    private static Moq.Language.Flow.ISetup<IDatabase, Task<bool>> SetupRedisWrite(Mock<IDatabase> cacheDb)
        => cacheDb.Setup(db => db.StringSetAsync(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()));

    private static void VerifyRedisWrite(Mock<IDatabase> cacheDb, Times times)
        => cacheDb.Verify(db => db.StringSetAsync(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), times);

    private static void VerifyNoRedisWrite(Mock<IDatabase> cacheDb)
    {
        VerifyRedisWrite(cacheDb, Times.Never());
        cacheDb.Verify(db => db.StringSetAsync(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    internal static X509Certificate2 CreateCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>
    /// One tenant with a certificate served from an absolute URL through a counting handler, so every
    /// download is visible. Downloads can be held open to observe concurrent requests.
    /// </summary>
    internal sealed class CertificateFixture : IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly byte[] _payload;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _downloads;

        public CertificateFixture(string subject, int validForDays = 30, int issuedDaysAgo = 1, string? password = null)
        {
            _certificate = CreateCertificate(subject);
            _payload = password == null ? _certificate.Export(X509ContentType.Cert) : _certificate.Export(X509ContentType.Pfx, password);
            TenantId = $"tenant-{Guid.NewGuid():N}";

            var tenants = new Mock<ITenants>();
            tenants.Setup(t => t.GetTenantTokenValidationParameter(TenantId)).Returns(new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = ["aud"],
                PublicCertificatePath = CertificateUrl,
                PublicCertificatePassword = password ?? string.Empty,
                PrivateCertificatePassword = string.Empty,
                IssueDate = DateTime.UtcNow.AddDays(-issuedDaysAgo),
                CertificateValidForNumberOfDays = validForDays
            });
            Tenants = tenants.Object;

            var handler = new CallbackHandler(ServeAsync);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
            HttpClientFactory = factory.Object;
        }

        public string TenantId { get; }
        public string CacheKey => $"tetocertpublic::{TenantId}";
        public string Thumbprint => _certificate.Thumbprint;
        public byte[] Payload => _payload;
        public ITenants Tenants { get; }
        public IHttpClientFactory HttpClientFactory { get; }
        public int Downloads => Volatile.Read(ref _downloads);
        public bool HoldDownloads { get; init; }
        public HttpStatusCode DownloadStatus { get; init; } = HttpStatusCode.OK;

        public void ReleaseDownloads() => _release.TrySetResult();

        public void Dispose() => _certificate.Dispose();

        private async Task<HttpResponseMessage> ServeAsync()
        {
            Interlocked.Increment(ref _downloads);
            if (HoldDownloads)
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30));

            return DownloadStatus == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_payload) }
                : new HttpResponseMessage(DownloadStatus);
        }
    }

    private sealed class CallbackHandler(Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond();
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JwtCertificateLogCaptureDefinition
{
    public const string Name = "JwtCertificateLogCapture";
}

/// <summary>
/// H9: the Redis-failure Warnings name the tenant and the operation and carry the Redis exception, but
/// never the certificate bytes or its password. Runs alone because it swaps the static Serilog logger.
/// </summary>
[Collection(JwtCertificateLogCaptureDefinition.Name)]
public sealed class JwtCertificateRedisFallbackLogTests : IDisposable
{
    private const string PasswordSentinel = "pfx-password-sentinel-131";

    private readonly Serilog.ILogger _previousLogger = Log.Logger;
    private readonly CapturingSink _sink = new();

    public JwtCertificateRedisFallbackLogTests()
    {
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
    }

    public void Dispose()
    {
        Log.Logger = _previousLogger;
    }

    [Fact]
    public async Task RedisReadFailure_ShouldLogWarningWithoutSecrets()
    {
        using var fixture = new JwtCertificateRedisFallbackTests.CertificateFixture("CN=log-read", password: PasswordSentinel);
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, $"GET {fixture.CacheKey}"));

        var result = await JwtBearerAuthenticationExtension.GetCertificateAsync(
            fixture.TenantId, fixture.Tenants, cacheDb.Object, fixture.HttpClientFactory, new(TimeProvider.System));

        Assert.NotNull(result);
        AssertSingleFailureWarning(fixture, "read", typeof(RedisConnectionException));
    }

    [Fact]
    public async Task RedisWriteFailure_ShouldLogWarningWithoutSecrets()
    {
        using var fixture = new JwtCertificateRedisFallbackTests.CertificateFixture("CN=log-write", password: PasswordSentinel);
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);
        cacheDb.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException($"Timeout awaiting response: SET {fixture.CacheKey}", CommandStatus.Sent));

        var result = await JwtBearerAuthenticationExtension.GetCertificateAsync(
            fixture.TenantId, fixture.Tenants, cacheDb.Object, fixture.HttpClientFactory, new(TimeProvider.System));

        Assert.NotNull(result);
        AssertSingleFailureWarning(fixture, "write", typeof(RedisTimeoutException));
    }

    private void AssertSingleFailureWarning(JwtCertificateRedisFallbackTests.CertificateFixture fixture, string operation, Type exceptionType)
    {
        var events = _sink.Events.Where(e => e.Properties.TryGetValue("TenantId", out var t) && t.ToString() == $"\"{fixture.TenantId}\"").ToList();
        var warning = Assert.Single(events, e => e.Properties.ContainsKey("Operation"));

        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Equal($"\"{operation}\"", warning.Properties["Operation"].ToString());
        Assert.IsType(exceptionType, warning.Exception);

        var payloadBase64 = Convert.ToBase64String(fixture.Payload);
        foreach (var logEvent in events)
        {
            var text = string.Join(
                "\n",
                logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture),
                logEvent.Exception?.ToString() ?? string.Empty,
                string.Join("\n", logEvent.Properties.Values.Select(v => v.ToString())));
            Assert.DoesNotContain(PasswordSentinel, text, StringComparison.Ordinal);
            Assert.DoesNotContain(payloadBase64[..40], text, StringComparison.Ordinal);
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

/// <summary>
/// Spec section 10 steps 1-3, in-process: the library's JWT bearer pipeline with a signed token, the
/// global exception handler in front, and a mocked Redis. Healthy Redis gives 200, a failing Redis with a
/// readable certificate path gives 200, and a failing Redis without a certificate gives 401; never 500.
/// </summary>
[Collection("BlocksAuthStaticState")]
public sealed class JwtCertificateRedisFallbackPipelineTests : IDisposable
{
    private const string Issuer = "issuer-131";
    private const string Audience = "audience-131";

    private readonly X509Certificate2 _signingCertificate = JwtCertificateRedisFallbackTests.CreateCertificate("CN=pipeline-131");
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"cert-131-{Guid.NewGuid():N}.cer");

    public JwtCertificateRedisFallbackPipelineTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
        File.WriteAllBytes(_certificatePath, _signingCertificate.Export(X509ContentType.Cert));
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
        File.Delete(_certificatePath);
        _signingCertificate.Dispose();
    }

    [Fact]
    public async Task Step1_HealthyRedis_ShouldAuthenticate()
    {
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(_signingCertificate.Export(X509ContentType.Cert));

        var status = await SendAsync(cacheDb, _certificatePath);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Step2_RedisDown_WithReadableCertificate_ShouldAuthenticate()
    {
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "No connection is active/available"));

        var status = await SendAsync(cacheDb, _certificatePath);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Step3_RedisDown_WithoutCertificate_ShouldRejectWith401()
    {
        var cacheDb = new Mock<IDatabase>();
        cacheDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException("Timeout awaiting response", CommandStatus.Sent));

        var status = await SendAsync(cacheDb, Path.Combine(Path.GetTempPath(), $"missing-131-{Guid.NewGuid():N}.cer"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    private async Task<HttpStatusCode> SendAsync(Mock<IDatabase> cacheDb, string certificatePath)
    {
        // A fresh tenant per request keeps the process-wide fallback cache out of the other tests.
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantTokenValidationParameter(tenantId)).Returns(new JwtTokenParameters
        {
            Issuer = Issuer,
            Subject = "subject",
            Audiences = [Audience],
            PublicCertificatePath = certificatePath,
            PublicCertificatePassword = string.Empty,
            PrivateCertificatePassword = string.Empty,
            IssueDate = DateTime.UtcNow.AddDays(-1),
            CertificateValidForNumberOfDays = 30
        });

        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.CacheDatabase()).Returns(cacheDb.Object);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(tenants.Object);
        builder.Services.AddSingleton(cacheClient.Object);
        // The library's authorization handlers need these; the endpoint only requires an authenticated user.
        builder.Services.AddSingleton(new Mock<ICryptoService>().Object);
        builder.Services.AddSingleton(new Mock<IDbContextProvider>().Object);
        builder.Services.AddSingleton(new ActivitySource("XUnitTest.Auth.CertificateFallback.Pipeline"));
        builder.Services.AddRouting();
        builder.Services.JwtBearerAuthentication();

        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/cert-check", () => Results.Text("ok")).RequireAuthorization();
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/cert-check");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateToken(tenantId));
        using var response = await app.GetTestClient().SendAsync(request);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        return response.StatusCode;
    }

    private string CreateToken(string tenantId)
    {
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: [new Claim(BlocksContext.TENANT_ID_CLAIM, tenantId), new Claim("sub", "user-131")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new X509SigningCredentials(_signingCertificate));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

#pragma warning restore CA1707, CA2007
