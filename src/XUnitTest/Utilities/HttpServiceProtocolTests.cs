using Blocks.Genesis;
using System.Diagnostics;
using System.Net;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace XUnitTest.Utilities;

public class HttpServiceProtocolTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public string? TraceParent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            TraceParent = request.Headers.TryGetValues("traceparent", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}")
            });
        }
    }

    [Fact]
    public async Task RawExchangePreservesTraceAndErrorWithoutRetrying()
    {
        using var source = new ActivitySource(Guid.NewGuid().ToString());
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => stopped = a
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("workflow").Start();
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://iam.test/api/oidc/token");
        using var response = await HttpService.SendProtocolAsync(client, request, source, default);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_grant", await response.Content.ReadAsStringAsync());
        Assert.NotNull(stopped);
        Assert.Equal(stopped.Id, request.Headers.GetValues("traceparent").Single());
        Assert.Equal(parent.TraceId, stopped.TraceId);
        Assert.Equal(parent.SpanId, stopped.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Error, stopped.Status);
        Assert.Equal(parent, Activity.Current);
    }
    private sealed class CallbackHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return send(ct);
        }
    }

    private sealed class Collector : BaseProcessor<Activity>
    {
        public readonly List<Activity> Spans = new();
        public override void OnEnd(Activity activity) => Spans.Add(activity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailurePreservesExceptionAndParentAndDoesNotRetry(bool cancel)
    {
        using var source = new ActivitySource(Guid.NewGuid().ToString());
        var collector = new Collector();
        using var provider = Sdk.CreateTracerProviderBuilder().SetSampler(new AlwaysOnSampler()).AddSource(source.Name).AddProcessor(collector).Build();
        using var parent = new Activity("workflow").Start();
        using var cts = new CancellationTokenSource();
        using var handler = new CallbackHandler(async ct =>
        {
            if (!cancel) throw new HttpRequestException("unreachable");
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://iam.test/token");
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpService.SendProtocolAsync(client, request, source, cts.Token));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => HttpService.SendProtocolAsync(client, request, source, cts.Token));
        var span = Assert.Single(collector.Spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(parent.TraceId, span.TraceId);
        Assert.Equal(parent, Activity.Current);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DefaultSourceReachesSdkProcessorAndStartsRootTrace()
    {
        var collector = new Collector();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .SetSampler(new AlwaysOnSampler()).AddSource(HttpService.ProtocolActivitySourceName).AddProcessor(collector).Build();
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://iam.test/discovery?secret=hidden");
        using var response = await HttpService.SendProtocolAsync(client, request, null, default);
        var span = Assert.Single(collector.Spans);
        Assert.Equal(span.Id, request.Headers.GetValues("traceparent").Single());
        Assert.Equal("https://iam.test/discovery", span.GetTagItem("url.full"));
    }

    [Fact]
    public async Task NormalHttpPathPropagatesTraceAndRecordsFailure()
    {
        using var source = new ActivitySource(Guid.NewGuid().ToString());
        var collector = new Collector();
        using var provider = Sdk.CreateTracerProviderBuilder().SetSampler(new AlwaysOnSampler()).AddSource(source.Name).AddProcessor(collector).Build();
        using var parent = new Activity("workflow").Start();
        using var handler = new Handler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var service = new HttpService(factory.Object, NullLogger<HttpService>.Instance, source);
        var (result, error) = await service.Get<object>("https://iam.test/api");
        Assert.Null(result);
        Assert.Contains("invalid_grant", error);
        var span = Assert.Single(collector.Spans);
        Assert.Equal(parent.TraceId, span.TraceId);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(span.Id, handler.TraceParent);
    }

    [Fact]
    public void OriginalPublicConstructorsRemainAvailable()
    {
        Assert.NotNull(typeof(DelegationTokenEndpointResolver).GetConstructor(new[]
        {
            typeof(IHttpClientFactory), typeof(Microsoft.Extensions.Logging.ILogger<DelegationTokenEndpointResolver>),
            typeof(Microsoft.Extensions.Configuration.IConfiguration)
        }));
        Assert.NotNull(typeof(Blocks.Genesis.Health.GenesisHealthPingBackgroundService).GetConstructor(new[]
        {
            typeof(Microsoft.Extensions.Logging.ILogger<Blocks.Genesis.Health.GenesisHealthPingBackgroundService>),
            typeof(IDbContextProvider), typeof(ICacheClient), typeof(IBlocksSecret)
        }));
    }

}
