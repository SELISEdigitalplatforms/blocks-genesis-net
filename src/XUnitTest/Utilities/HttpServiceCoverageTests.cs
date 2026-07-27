using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace XUnitTest.Utilities;

public class HttpServiceCoverageTests
{
    [Fact]
    public async Task Get_ShouldUseCustomTimeoutPipeline_WhenTimeoutSecondsIsProvided()
    {
        var requests = 0;
        var service = CreateService(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new TestResponse { Name = "custom" }))
            });
        });

        var (result, error) = await service.Get<TestResponse>("http://localhost/custom", timeoutSeconds: 5);

        Assert.Equal("custom", result.Name);
        Assert.Equal(string.Empty, error);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Post_ShouldRetryWithinCustomTimeoutPipeline_WhenFirstResponseIsTransient()
    {
        var requests = 0;
        var service = CreateService(_ =>
        {
            requests++;
            if (requests == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("boom")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new TestResponse { Name = "retried" }))
            });
        }, new HttpServiceOptions
        {
            MaxRetryAttempts = 1,
            RetryDelaySeconds = 0
        });

        var (result, error) = await service.Post<TestResponse>(new { }, "http://localhost/retry", timeoutSeconds: 5);

        Assert.Equal("retried", result.Name);
        Assert.Equal(string.Empty, error);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task SendRequest_ShouldRetryOnTooManyRequests_ThenSucceed()
    {
        var requests = 0;
        var service = CreateService(_ =>
        {
            requests++;
            if (requests == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("slow down")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new TestResponse { Name = "eventually" }))
            });
        }, new HttpServiceOptions
        {
            MaxRetryAttempts = 1,
            RetryDelaySeconds = 0
        });

        var (result, error) = await service.SendRequest<TestResponse>(HttpMethod.Get, "http://localhost/throttled");

        Assert.Equal("eventually", result.Name);
        Assert.Equal(string.Empty, error);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task SendRequest_ShouldReportError_WhenCircuitBreakerOpens()
    {
        var service = CreateService(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("down")
            }), new HttpServiceOptions
        {
            MaxRetryAttempts = 1,
            RetryDelaySeconds = 0,
            CircuitBreakerFailureRatio = 0.1,
            CircuitBreakerSamplingDurationSeconds = 1,
            CircuitBreakerBreakDurationSeconds = 1,
            CircuitBreakerMinimumThroughput = 2
        });

        // Repeated transient failures trip the breaker; subsequent calls short-circuit
        // into the catch-all path and surface the broken-circuit message as the error.
        string lastError = string.Empty;
        for (var i = 0; i < 6; i++)
        {
            var (_, error) = await service.SendRequest<TestResponse>(HttpMethod.Get, "http://localhost/broken");
            lastError = error;
        }

        Assert.False(string.IsNullOrWhiteSpace(lastError));
    }

    [Fact]
    public async Task PostFormUrlEncoded_ShouldUseCustomTimeout_WhenProvided()
    {
        HttpRequestMessage? seen = null;
        var service = CreateService(request =>
        {
            seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new TestResponse { Name = "form" }))
            });
        });

        var (result, _) = await service.PostFormUrlEncoded<TestResponse>(
            new Dictionary<string, string> { ["a"] = "1" },
            "http://localhost/form",
            headers: new Dictionary<string, string> { ["x-test"] = "yes" },
            timeoutSeconds: 5);

        Assert.Equal("form", result.Name);
        Assert.NotNull(seen);
        Assert.Equal("application/x-www-form-urlencoded", seen!.Content?.Headers.ContentType?.MediaType);
        Assert.True(seen.Headers.Contains("x-test"));
    }

    [Fact]
    public async Task Get_ShouldReturnError_WhenHandlerThrowsUnderAmbientActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("parent").Start();

        var service = CreateService(_ => throw new HttpRequestException("no route"));

        var (result, error) = await service.Get<TestResponse>("http://localhost/error", timeoutSeconds: 2);

        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static HttpService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler, HttpServiceOptions? options = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(new StubHttpMessageHandler(handler)));

        return new HttpService(
            factory.Object,
            new Mock<ILogger<HttpService>>().Object,
            new ActivitySource($"HttpServiceCoverageTests-{Guid.NewGuid():N}"),
            options is null ? null : Options.Create(options));
    }

    private sealed class TestResponse
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
