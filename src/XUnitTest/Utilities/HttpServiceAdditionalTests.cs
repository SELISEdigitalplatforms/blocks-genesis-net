using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace XUnitTest.Utilities;

public class HttpServiceAdditionalTests
{
    [Fact]
    public async Task SendRequest_ShouldInvokeRetryCallback_WhenTransientFailureRecovers()
    {
        // First call returns 500 (retry-triggering), second returns 200. Polly's base
        // delay is 2s with ~50% jitter, so this test completes in roughly 1-3s after
        // the first attempt.
        var attempts = 0;
        var handler = new RetryStubHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("transient", Encoding.UTF8, "text/plain")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"recovered\"}", Encoding.UTF8, "application/json")
            });
        });

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-http-retry",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { }
        };
        ActivitySource.AddActivityListener(activityListener);

        var logger = new Mock<ILogger<HttpService>>();
        // Source-generated [LoggerMessage] methods short-circuit when IsEnabled
        // returns false (Moq's default), so explicitly enable all levels.
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var service = CreateService(handler, logger, "test-http-retry");

        var (result, error) = await service.Get<TestResponse>("https://example.com/transient");

        Assert.NotNull(result);
        Assert.Equal("recovered", result!.Name);
        Assert.Equal(string.Empty, error);
        Assert.Equal(2, attempts);

        // OnRetry invokes HttpServiceLog.HttpRetry (LogLevel.Warning, EventId 5001).
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.Is<EventId>(e => e.Id == 5001),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private static HttpService CreateService(HttpMessageHandler handler, Mock<ILogger<HttpService>> logger, string sourceName)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler, disposeHandler: false));
        return new HttpService(factory.Object, logger.Object, new ActivitySource(sourceName));
    }

    private sealed class TestResponse
    {
        public string? Name { get; set; }
    }

    private sealed class RetryStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public RetryStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}
