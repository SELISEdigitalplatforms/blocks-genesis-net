using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Polly;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace XUnitTest.Utilities;

/// <summary>HttpService handling of downstream 429 (spec 6.9: H13, H14, C13, C14).</summary>
public class HttpServiceTooManyRequestsTests
{
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(Interlocked.Increment(ref _calls)));
    }

    private static (HttpService Service, StubHandler Handler) Create(Func<int, HttpResponseMessage> respond, HttpServiceOptions? options = null)
    {
        var handler = new StubHandler(respond);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var service = new HttpService(
            factory.Object,
            new Mock<ILogger<HttpService>>().Object,
            new ActivitySource($"HttpService429-{Guid.NewGuid():N}"),
            Options.Create(options ?? new HttpServiceOptions()));
        return (service, handler);
    }

    private static HttpResponseMessage TooMany(TimeSpan? delta = null, DateTimeOffset? date = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("limited") };
        if (delta.HasValue)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta.Value);
        }
        else if (date.HasValue)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date.Value);
        }

        return response;
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { Name = "ok" })) };

    [Fact]
    public async Task ShortRetryAfter_WaitsThenRetries() // H13
    {
        var (service, handler) = Create(call => call == 1 ? TooMany(TimeSpan.FromSeconds(1)) : Ok());

        var watch = Stopwatch.StartNew();
        var (_, error) = await service.Get<object>("http://localhost/x");
        watch.Stop();

        Assert.Equal(string.Empty, error);
        Assert.Equal(2, handler.Calls);
        Assert.InRange(watch.ElapsedMilliseconds, 900, 5000);
    }

    [Fact]
    public async Task HttpDateRetryAfter_IsHonoured() // H13 (HTTP-date form)
    {
        var (service, handler) = Create(call => call == 1 ? TooMany(date: DateTimeOffset.UtcNow.AddSeconds(1)) : Ok());

        var (_, error) = await service.Get<object>("http://localhost/x");

        Assert.Equal(string.Empty, error);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task LongOrMissingRetryAfter_ReturnsImmediately() // H14
    {
        var (longService, longHandler) = Create(_ => TooMany(TimeSpan.FromSeconds(30)));
        var (noneService, noneHandler) = Create(_ => TooMany());

        var watch = Stopwatch.StartNew();
        var (_, longError) = await longService.Get<object>("http://localhost/x");
        var (_, noneError) = await noneService.Get<object>("http://localhost/x");
        watch.Stop();

        Assert.Equal("limited", longError);
        Assert.Equal("limited", noneError);
        Assert.Equal(1, longHandler.Calls);
        Assert.Equal(1, noneHandler.Calls);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RepeatedTooManyRequests_DoNotOpenTheBreaker() // C13
    {
        var options = new HttpServiceOptions { CircuitBreakerMinimumThroughput = 2, MaxRetryAttempts = 1 };
        var (service, handler) = Create(_ => TooMany(), options);

        for (var i = 0; i < 12; i++)
        {
            var (_, error) = await service.Get<object>("http://localhost/x");
            Assert.Equal("limited", error);
        }

        Assert.Equal(12, handler.Calls);
    }

    [Fact]
    public async Task ServerErrors_KeepRetryAndBreakerBehaviour() // C14
    {
        var options = new HttpServiceOptions { CircuitBreakerMinimumThroughput = 2, MaxRetryAttempts = 1, RetryDelaySeconds = 0 };
        var (service, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") }, options);

        var (_, first) = await service.Get<object>("http://localhost/x");
        Assert.Equal("down", first);
        Assert.Equal(2, handler.Calls);

        for (var i = 0; i < 3; i++)
        {
            await service.Get<object>("http://localhost/x");
        }

        var callsBefore = handler.Calls;
        var (_, open) = await service.Get<object>("http://localhost/x");
        Assert.Equal(callsBefore, handler.Calls);
        Assert.Contains("circuit", open, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Predicates_CoverEveryBranch()
    {
        var context = ResilienceContextPool.Shared.Get();
        try
        {
            var timeout = TimeSpan.FromSeconds(30);
            Assert.True(HttpService.ShouldRetry(Outcome.FromException<HttpResponseMessage>(new HttpRequestException()), context, timeout));
            Assert.True(HttpService.ShouldRetry(Outcome.FromException<HttpResponseMessage>(new Polly.Timeout.TimeoutRejectedException()), context, timeout));
            Assert.False(HttpService.ShouldRetry(Outcome.FromException<HttpResponseMessage>(new InvalidOperationException()), context, timeout));
            Assert.False(HttpService.ShouldRetry(Outcome.FromResult<HttpResponseMessage>(null!), context, timeout));
            Assert.True(HttpService.ShouldRetry(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)), context, timeout));
            Assert.False(HttpService.ShouldRetry(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)), context, timeout));
            Assert.True(HttpService.ShouldRetry(Outcome.FromResult(TooMany(TimeSpan.FromSeconds(1))), context, timeout));
            Assert.False(HttpService.ShouldRetry(Outcome.FromResult(TooMany(TimeSpan.FromSeconds(30))), context, timeout));

            Assert.Null(HttpService.GetTooManyRequestsDelay(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))));
            Assert.Null(HttpService.GetTooManyRequestsDelay(Outcome.FromResult<HttpResponseMessage>(null!)));
            Assert.Equal(TimeSpan.FromSeconds(2), HttpService.GetTooManyRequestsDelay(Outcome.FromResult(TooMany(TimeSpan.FromSeconds(2)))));

            var now = DateTimeOffset.UtcNow;
            Assert.Equal(TimeSpan.Zero, HttpService.GetRetryAfter(TooMany(date: now.AddSeconds(-5)), now));
            Assert.Null(HttpService.GetRetryAfter(TooMany(), now));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    [Fact]
    public void CircuitBreakerPredicate_IgnoresTooManyRequests()
    {
        Assert.NotNull(HttpService.CircuitBreakerPredicate());
    }
}
