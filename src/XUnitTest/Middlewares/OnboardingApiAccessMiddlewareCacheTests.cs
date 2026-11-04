using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Connections;
using Moq;
using System.Net;
using XUnitTest.TestDoubles;

namespace XUnitTest.Middlewares;

#pragma warning disable CA1707, CA2007 // same naming and await style as the middleware tests next to this file

/// <summary>
/// Cache, failure and concurrency behaviour of <see cref="OnboardingApiAccessMiddleware"/> through its internal
/// seam: a hand-written counting source (Moq cannot proxy the internal interface) and a manual clock.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class OnboardingApiAccessMiddlewareCacheTests
{
    private const string RootTenant = "root";
    private const string ProjectTenant = "proj-1";
    private const string ForbiddenBody = "{\"Errors\":{\"Message\":\"Forbidden: Cross_Tenant_Access_Not_Allowed\"},\"IsSuccess\":false}";
    private const string UnavailableBody = "{\"Errors\":{\"Message\":\"Service_Unavailable: Os_Allowed_Apis_Unavailable\"},\"IsSuccess\":false}";
    private const string LastGoodWarning = "serving the last good list for 30 s";
    private const string UnavailableWarning = "root cross-tenant requests get 503 for 30 s";

    [Fact]
    public void Constants_ShouldMatchTheSpecifiedLifetimes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), OnboardingApiAccessMiddleware.AllowedApisCacheLifetime);
        Assert.Equal(TimeSpan.FromSeconds(2), OnboardingApiAccessMiddleware.AllowedApisLoadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), OnboardingApiAccessMiddleware.AllowedApisFailureRetryDelay);
    }

    [Fact]
    public void InternalConstructor_ShouldThrow_WhenSourceOrTimeProviderIsNull()
    {
        var tenants = Mock.Of<ITenants>();
        var logger = Mock.Of<ILogger<OnboardingApiAccessMiddleware>>();

        Assert.Equal("next", Assert.Throws<ArgumentNullException>(() =>
            new OnboardingApiAccessMiddleware(null!, tenants, logger, new CountingSource(), TimeProvider.System)).ParamName);
        Assert.Equal("tenants", Assert.Throws<ArgumentNullException>(() =>
            new OnboardingApiAccessMiddleware(_ => Task.CompletedTask, null!, logger, new CountingSource(), TimeProvider.System)).ParamName);
        Assert.Equal("logger", Assert.Throws<ArgumentNullException>(() =>
            new OnboardingApiAccessMiddleware(_ => Task.CompletedTask, tenants, null!, new CountingSource(), TimeProvider.System)).ParamName);
        Assert.Equal("source", Assert.Throws<ArgumentNullException>(() =>
            new OnboardingApiAccessMiddleware(_ => Task.CompletedTask, tenants, logger, null!, TimeProvider.System)).ParamName);
        Assert.Equal("timeProvider", Assert.Throws<ArgumentNullException>(() =>
            new OnboardingApiAccessMiddleware(_ => Task.CompletedTask, tenants, logger, new CountingSource(), null!)).ParamName);
    }

    // E1, H1, C9
    [Fact]
    public async Task SameTenant_ShouldCallNext_WithoutTenantLookupOrSourceCall_EvenWhileSourceThrows()
    {
        using var harness = new Harness();
        harness.Source.Throws(NewConnectionException());

        var result = await harness.SendAsync("/api/private", tenantId: ProjectTenant, originalTenantId: ProjectTenant);

        Assert.True(result.NextCalled);
        Assert.Equal(0, harness.Source.Calls);
        harness.Tenants.Verify(t => t.GetTenantByID(It.IsAny<string>()), Times.Never);
    }

    // E2, H2, P1, C9: non-root, unknown and blank tenants, while the source throws.
    [Theory]
    [InlineData("plain")]
    [InlineData("ghost")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task NonRootCrossTenant_ShouldCallNext_WithoutSourceCall_EvenWhileSourceThrows(string tenantId)
    {
        using var harness = new Harness();
        harness.Source.Throws(NewConnectionException());

        var result = await harness.SendAsync("/api/private", tenantId: tenantId, originalTenantId: ProjectTenant);

        Assert.True(result.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
        Assert.Equal(0, harness.Source.Calls);
    }

    // H3, P1: requests that never reach the tenant check.
    [Fact]
    public async Task NonControllerEndpointOrMissingContext_ShouldCallNext_WithoutTenantLookupOrSourceCall()
    {
        using var harness = new Harness();
        harness.Source.Throws(NewConnectionException());

        var noEndpoint = await harness.SendAsync("/api/private", endpointName: null, setEndpoint: false);
        var healthEndpoint = await harness.SendAsync("/api/private", endpointName: "Health check probe");
        var noContext = await harness.SendAsync("/api/private", setContext: false);

        Assert.True(noEndpoint.NextCalled);
        Assert.True(healthEndpoint.NextCalled);
        Assert.True(noContext.NextCalled);
        Assert.Equal(0, harness.Source.Calls);
        harness.Tenants.Verify(t => t.GetTenantByID(It.IsAny<string>()), Times.Never);
    }

    // E3, E4, H4, H5
    [Fact]
    public async Task RootCrossTenant_ShouldAllowListedPath_AndRejectOthersWithExactBody_FromOneLoad()
    {
        using var harness = new Harness();
        harness.Source.Returns("api/allowed");

        var allowed = await harness.SendAsync("/API/Allowed/");
        var rejected = await harness.SendAsync("/api/private");

        Assert.True(allowed.NextCalled);
        Assert.False(rejected.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, rejected.Context.Response.StatusCode);
        Assert.Equal(ForbiddenBody, rejected.Body);
        Assert.False(rejected.Context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal(1, harness.Source.Calls);
        Assert.Equal(1, harness.WarningCount("Blocked cross-tenant onboarding API access"));
    }

    // C14
    [Fact]
    public async Task RootCrossTenant_ShouldRejectRootPath_WhenListIsLoaded()
    {
        using var harness = new Harness();
        harness.Source.Returns("api/allowed");

        var result = await harness.SendAsync("/");

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
    }

    // E5, H7, H9, P3
    [Fact]
    public async Task MissingDocument_ShouldBeCachedAsEmptyListForFiveMinutes()
    {
        using var harness = new Harness();
        harness.Source.ReturnsDocument(null);

        var first = await harness.SendAsync("/api/anything");
        Assert.Equal(1, harness.Source.Calls);

        harness.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var beforeExpiry = await harness.SendAsync("/api/anything");
        Assert.Equal(1, harness.Source.Calls);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        var atExpiry = await harness.SendAsync("/api/anything");

        Assert.All(new[] { first, beforeExpiry, atExpiry }, r => Assert.Equal(StatusCodes.Status403Forbidden, r.Context.Response.StatusCode));
        Assert.Equal(2, harness.Source.Calls);
    }

    // H9: every shape that is not a usable array is a successful, empty load.
    [Theory]
    [MemberData(nameof(EmptyListDocuments))]
    public async Task UnusableAllowedApis_ShouldGive403_AndNotReloadPerRequest(BsonDocument document)
    {
        using var harness = new Harness();
        harness.Source.ReturnsDocument(document);

        for (var i = 0; i < 5; i++)
        {
            var result = await harness.SendAsync("/api/anything");
            Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
        }

        Assert.Equal(1, harness.Source.Calls);
        Assert.Equal(0, harness.WarningCount("could not be loaded"));
    }

    public static TheoryData<BsonDocument> EmptyListDocuments() => new()
    {
        new BsonDocument { { "Other", 1 } },
        new BsonDocument { { "AllowedApis", "api/anything" } },
        new BsonDocument { { "AllowedApis", new BsonArray() } },
    };

    // P3
    [Fact]
    public async Task ManyRequestsWithinLifetime_ShouldCauseOneSourceCall()
    {
        using var harness = new Harness();
        harness.Source.Returns("api/allowed");

        for (var i = 0; i < 500; i++)
        {
            var result = await harness.SendAsync(i % 2 == 0 ? "/api/allowed" : "/api/private");
            Assert.Equal(i % 2 == 0, result.NextCalled);
            harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(1, harness.Source.Calls);
    }

    // H8: the reload at 5 min applies the new list to the request that triggered it.
    [Fact]
    public async Task ExpiredList_ShouldReload_AndApplyAddedEntryToTriggeringRequest()
    {
        using var harness = new Harness();
        harness.Source.Returns("api/a");

        await harness.SendAsync("/api/a");
        harness.Source.Returns("api/a", "api/new");

        harness.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var beforeExpiry = await harness.SendAsync("/api/new");
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        var atExpiry = await harness.SendAsync("/api/new");

        Assert.Equal(StatusCodes.Status403Forbidden, beforeExpiry.Context.Response.StatusCode);
        Assert.True(atExpiry.NextCalled);
        Assert.Equal(2, harness.Source.Calls);
    }

    // E6, C1, C2, C7, C8, P4
    [Fact]
    public async Task ColdFailure_ShouldGive503WithRetryAfter_AndRetryOnlyAfter30Seconds()
    {
        using var harness = new Harness();
        harness.Source.Throws(NewConnectionException("connect to mongodb://admin:hunter2@root-db:27017 failed"));

        var first = await harness.SendAsync("/api/allowed");

        Assert.False(first.NextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, first.Context.Response.StatusCode);
        Assert.Equal("30", first.Context.Response.Headers.RetryAfter.ToString());
        Assert.Equal(UnavailableBody, first.Body);
        Assert.Equal(1, harness.Source.Calls);
        var warning = Assert.Single(harness.Warnings);
        Assert.Contains("MongoConnectionException", warning.Message, StringComparison.Ordinal);
        Assert.Contains(UnavailableWarning, warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain("hunter2", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("root-db", warning.Message, StringComparison.Ordinal);

        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        var inWindow = await harness.SendAsync("/api/allowed");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, inWindow.Context.Response.StatusCode);
        Assert.Equal(1, harness.Source.Calls);
        Assert.Single(harness.Warnings); // no per-request log for a 503

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        var afterWindow = await harness.SendAsync("/api/allowed");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, afterWindow.Context.Response.StatusCode);
        Assert.Equal(2, harness.Source.Calls);
        Assert.Equal(2, harness.Warnings.Count); // exactly one per failed load
    }

    // A source that throws synchronously instead of returning a faulted task is handled the same way.
    [Fact]
    public async Task SynchronousSourceThrow_ShouldGive503_NotAnException()
    {
        using var harness = new Harness();
        harness.Source.ThrowsSynchronously(new InvalidOperationException("boom"));

        var result = await harness.SendAsync("/api/allowed");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Context.Response.StatusCode);
        Assert.Contains("InvalidOperationException", Assert.Single(harness.Warnings).Message, StringComparison.Ordinal);
    }

    // E7, C3, C8
    [Fact]
    public async Task FailedReload_ShouldKeepServingLastGoodList_AndRetryAfter30Seconds()
    {
        using var harness = new Harness();
        harness.Source.Returns("api/allowed");
        await harness.SendAsync("/api/allowed");

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        harness.Source.Throws(NewConnectionException());
        var allowed = await harness.SendAsync("/api/allowed");
        var rejected = await harness.SendAsync("/api/private");

        Assert.True(allowed.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, rejected.Context.Response.StatusCode);
        Assert.Equal(ForbiddenBody, rejected.Body);
        var warning = Assert.Single(harness.Warnings, w => w.Message.Contains("could not be loaded", StringComparison.Ordinal));
        Assert.Contains("MongoConnectionException", warning.Message, StringComparison.Ordinal);
        Assert.Contains(LastGoodWarning, warning.Message, StringComparison.Ordinal);
        Assert.Equal(2, harness.Source.Calls);

        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        await harness.SendAsync("/api/allowed");
        Assert.Equal(2, harness.Source.Calls);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        var afterWindow = await harness.SendAsync("/api/allowed");
        Assert.True(afterWindow.NextCalled); // still the last good list after a second failure
        Assert.Equal(3, harness.Source.Calls);
    }

    // E8, C4, P5
    [Fact]
    public async Task HangingLoad_ShouldTimeOutAfterTwoSeconds_CancelTheRead_AndDiscardItsLateResult()
    {
        using var harness = new Harness();
        var gate = harness.Source.Gate();

        var pending = harness.SendAsync("/api/allowed");
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, harness.Clock.PendingTimers);

        harness.Clock.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(1));
        Assert.False(pending.IsCompleted);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var timedOut = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, timedOut.Context.Response.StatusCode);
        Assert.Equal(UnavailableBody, timedOut.Body);
        Assert.True(harness.Source.LastToken.IsCancellationRequested);
        var warning = Assert.Single(harness.Warnings);
        Assert.Contains("TimeoutException", warning.Message, StringComparison.Ordinal);
        Assert.Contains(UnavailableWarning, warning.Message, StringComparison.Ordinal);

        // The late result is discarded: the failure entry stays until t = 2 s + 30 s.
        gate.SetResult(new BsonDocument { { "AllowedApis", new BsonArray { "api/allowed" } } });
        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        var atT31 = await harness.SendAsync("/api/allowed");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, atT31.Context.Response.StatusCode);
        Assert.Equal(1, harness.Source.Calls);

        harness.Source.Returns("api/allowed");
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        var atT32 = await harness.SendAsync("/api/allowed");
        Assert.True(atT32.NextCalled);
        Assert.Equal(2, harness.Source.Calls);
    }

    // C4: an abandoned read that faults later is observed, so it never raises UnobservedTaskException.
    [Fact]
    public async Task HangingLoadThatFaultsLater_ShouldBeObserved()
    {
        var marker = $"late-fault-{Guid.NewGuid():N}";
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x.Message == marker))
            {
                Interlocked.Increment(ref unobserved);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await TimeOutThenFaultAsync(marker);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // Kept out of line so nothing in the test method still references the faulted read when the GC runs.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task TimeOutThenFaultAsync(string marker)
    {
        using var harness = new Harness();
        var gate = harness.Source.Gate();
        var pending = harness.SendAsync("/api/allowed");
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Context.Response.StatusCode);

        gate.SetException(new IOException(marker));
        // Continuations were queued asynchronously; give the observing one time to run.
        await Task.Delay(100);
    }

    // C10, P4
    [Fact]
    public async Task RecoveryAfterFailure_ShouldReplaceFailureEntryForFiveMinutes()
    {
        using var harness = new Harness();
        harness.Source.Throws(NewConnectionException());
        var failed = await harness.SendAsync("/api/allowed");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failed.Context.Response.StatusCode);

        harness.Source.Returns("api/allowed");
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        var recovered = await harness.SendAsync("/api/allowed");
        var rejected = await harness.SendAsync("/api/private");
        Assert.True(recovered.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, rejected.Context.Response.StatusCode);
        Assert.Equal(2, harness.Source.Calls);

        harness.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.True((await harness.SendAsync("/api/allowed")).NextCalled);
        Assert.Equal(2, harness.Source.Calls);
    }

    // E9, H10, C11
    [Fact]
    public async Task MixedElements_ShouldKeepValidStrings_AndSkipTheRest()
    {
        using var harness = new Harness();
        harness.Source.ReturnsDocument(new BsonDocument
        {
            { "AllowedApis", new BsonArray { "api/a", 5, BsonNull.Value, "", "  ", new BsonDocument("x", 1), "API/B" } },
        });

        var a = await harness.SendAsync("/api/a");
        var b = await harness.SendAsync("/api/b");
        var five = await harness.SendAsync("/5");

        Assert.True(a.NextCalled);
        Assert.True(b.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, five.Context.Response.StatusCode);
        Assert.Equal(1, harness.Source.Calls);
        Assert.Equal(0, harness.WarningCount("could not be loaded")); // a success, and nothing logged about skipped entries
    }

    // E11, C5, P2
    [Fact]
    public async Task ConcurrentColdRequests_ShouldShareOneLoad()
    {
        const int requests = 200;
        using var harness = new Harness();
        var gate = harness.Source.Gate();
        var pastTenantCheck = 0;
        var allPastTenantCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tenants.Setup(t => t.GetTenantByID(RootTenant))
            .Returns(() => MakeTenant(RootTenant, isRootTenant: true))
            .Callback(() =>
            {
                if (Interlocked.Increment(ref pastTenantCheck) == requests)
                {
                    allPastTenantCheck.SetResult();
                }
            });

        var pending = Enumerable.Range(0, requests)
            .Select(_ => Task.Run(() => harness.SendAsync("/api/allowed")))
            .ToArray();

        // Awaited, not blocked on, so the test holds no thread-pool thread while the requests queue up.
        await allPastTenantCheck.Task.WaitAsync(TimeSpan.FromSeconds(30));
        gate.SetResult(new BsonDocument { { "AllowedApis", new BsonArray { "api/allowed" } } });
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, harness.Source.Calls);
        Assert.All(results, r => Assert.True(r.NextCalled));
    }

    // C6: a reload of an expired entry is shared, and one caller's abort does not cancel it.
    [Fact]
    public async Task ReloadInFlight_ShouldBeJoined_AndNotCancelledByOneRequestAbort()
    {
        using var harness = new Harness();
        harness.Source.Returns("api/old");
        await harness.SendAsync("/api/old");

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var gate = harness.Source.Gate();
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var first = harness.SendAsync("/api/new", requestAborted: aborted.Token);
        var second = harness.SendAsync("/api/new");
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(2, harness.Source.Calls);
        Assert.False(harness.Source.LastToken.IsCancellationRequested);

        gate.SetResult(new BsonDocument { { "AllowedApis", new BsonArray { "api/new" } } });
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, r => Assert.True(r.NextCalled));
        Assert.Equal(2, harness.Source.Calls);
        Assert.Empty(harness.Warnings);
    }

    private static MongoConnectionException NewConnectionException(string message = "connection refused")
    {
        return new MongoConnectionException(
            new ConnectionId(new MongoDB.Driver.Core.Servers.ServerId(
                new MongoDB.Driver.Core.Clusters.ClusterId(), new DnsEndPoint("localhost", 27017))),
            message);
    }

    private static Blocks.Genesis.Tenant MakeTenant(string tenantId, bool isRootTenant)
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            IsRootTenant = isRootTenant,
            DbConnectionString = "mongodb://127.0.0.1:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer", Subject = "subject", Audiences = [],
                PublicCertificatePath = "path", PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty, IssueDate = DateTime.UtcNow
            }
        };
    }

    private sealed record RequestResult(DefaultHttpContext Context, bool NextCalled, string Body);

    private sealed record LoggedWarning(string Message, Exception? Exception);

    /// <summary>One middleware with a counting source, a manual clock and a shared root-tenant test context.</summary>
    private sealed class Harness : IDisposable
    {
        private const string NextCalledKey = "next-called";
        private readonly bool _originalTestMode = BlocksContext.IsTestMode;

        public Harness()
        {
            BlocksContext.IsTestMode = true;
            Tenants.Setup(t => t.GetTenantByID(RootTenant)).Returns(MakeTenant(RootTenant, isRootTenant: true));
            Tenants.Setup(t => t.GetTenantByID("plain")).Returns(MakeTenant("plain", isRootTenant: false));
            // Source-generated log methods check IsEnabled first.
            Logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            Middleware = new OnboardingApiAccessMiddleware(
                ctx => { ctx.Items[NextCalledKey] = true; return Task.CompletedTask; },
                Tenants.Object, Logger.Object, Source, Clock);
        }

        public CountingSource Source { get; } = new();

        public ManualClock Clock { get; } = new();

        public Mock<ITenants> Tenants { get; } = new();

        public Mock<ILogger<OnboardingApiAccessMiddleware>> Logger { get; } = new();

        public OnboardingApiAccessMiddleware Middleware { get; }

        public List<LoggedWarning> Warnings => Logger.Invocations
            .Where(i => i.Method.Name == nameof(ILogger.Log) && (LogLevel)i.Arguments[0] == LogLevel.Warning)
            .Select(i => new LoggedWarning(i.Arguments[2].ToString()!, (Exception?)i.Arguments[3]))
            .ToList();

        public int WarningCount(string text) => Warnings.Count(w => w.Message.Contains(text, StringComparison.Ordinal));

        public async Task<RequestResult> SendAsync(
            string path,
            string tenantId = RootTenant,
            string originalTenantId = ProjectTenant,
            string? endpointName = "Sample.Controller.Action",
            bool setEndpoint = true,
            bool setContext = true,
            CancellationToken requestAborted = default)
        {
            // Static test-mode context: every request in a test shares it, and tests in this collection run serially.
            if (setContext)
            {
                BlocksContext.SetContext(BlocksContext.Create(
                    tenantId, [], "", true, "", "", DateTime.MinValue, "", [], "", "", "", "", originalTenantId));
            }
            else
            {
                BlocksContext.ClearContext();
            }

            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.RequestAborted = requestAborted;
            context.Response.Body = new MemoryStream();
            if (setEndpoint)
            {
                context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, endpointName));
            }

            await Middleware.InvokeAsync(context);

            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            return new RequestResult(context, context.Items.ContainsKey(NextCalledKey), await reader.ReadToEndAsync(CancellationToken.None));
        }

        public void Dispose()
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = _originalTestMode;
        }
    }

    /// <summary>A hand-written <see cref="IOsAllowedApiSource"/> that counts calls and can return, throw or hang.</summary>
    private sealed class CountingSource : IOsAllowedApiSource
    {
        private int _calls;
        private Func<Task<BsonDocument?>> _read = () => Task.FromResult<BsonDocument?>(null);

        public int Calls => Volatile.Read(ref _calls);

        public CancellationToken LastToken { get; private set; }

        public void Returns(params string[] allowedApis) =>
            ReturnsDocument(new BsonDocument { { "AllowedApis", new BsonArray(allowedApis) } });

        public void ReturnsDocument(BsonDocument? document) => _read = () => Task.FromResult(document);

        public void Throws(Exception exception) => _read = () => Task.FromException<BsonDocument?>(exception);

        public void ThrowsSynchronously(Exception exception) => _read = () => throw exception;

        public TaskCompletionSource<BsonDocument?> Gate()
        {
            var gate = new TaskCompletionSource<BsonDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _read = () => gate.Task;
            return gate;
        }

        public Task<BsonDocument?> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastToken = cancellationToken;
            return _read();
        }
    }
}
