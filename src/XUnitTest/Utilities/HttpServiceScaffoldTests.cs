using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace XUnitTest.Utilities;

public class HttpServiceScaffoldTests
{
    [Fact]
    public async Task Get_ShouldDeserializeSuccessfulJsonResponse()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"alice\"}", Encoding.UTF8, "application/json")
            }));

        var service = CreateService(handler);

        var (result, error) = await service.Get<TestResponse>("https://example.com/users/1");

        Assert.NotNull(result);
        Assert.Equal("alice", result!.Name);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public async Task SendRequest_ShouldAttachHeaders_AndSendJsonBody()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            capturedBody = request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json")
            });
        });

        var service = CreateService(handler);
        var payload = new { Name = "bob" };

        var (result, error) = await service.SendRequest<TestResponse>(
            HttpMethod.Post,
            "https://example.com/users",
            payload,
            "application/json",
            new Dictionary<string, string> { ["X-Correlation-Id"] = "cid-1" });

        Assert.NotNull(result);
        Assert.Equal("ok", result!.Name);
        Assert.Equal(string.Empty, error);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.True(captured.Headers.TryGetValues("X-Correlation-Id", out var values));
        Assert.Contains("cid-1", values!);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("bob", doc.RootElement.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task PostFormUrlEncoded_ShouldSendFormBody()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"done\"}", Encoding.UTF8, "application/json")
            });
        });

        var service = CreateService(handler);

        var (result, error) = await service.PostFormUrlEncoded<TestResponse>(
            new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "api.read" },
            "https://example.com/token");

        Assert.NotNull(result);
        Assert.Equal("done", result!.Name);
        Assert.Equal(string.Empty, error);
        Assert.NotNull(captured);
        Assert.Equal("application/x-www-form-urlencoded", captured!.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Get_ShouldReturnError_WhenRequestThrows()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("network-down"));
        var service = CreateService(handler);

        var (result, error) = await service.Get<TestResponse>("https://example.com/failure");

        Assert.Null(result);
        Assert.Contains("network-down", error);
    }

    private static HttpService CreateService(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler, disposeHandler: false));

        var logger = new Mock<ILogger<HttpService>>();
        return new HttpService(factory.Object, logger.Object, new System.Diagnostics.ActivitySource("test-http"));
    }

    private sealed class TestResponse
    {
        public string? Name { get; set; }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
