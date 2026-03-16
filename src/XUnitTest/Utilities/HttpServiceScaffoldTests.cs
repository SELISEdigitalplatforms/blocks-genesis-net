using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Reflection;

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
    public async Task Post_ShouldDeserializeSuccessfulJsonResponse()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"posted\"}", Encoding.UTF8, "application/json")
            }));

        var service = CreateService(handler);

        var (result, error) = await service.Post<TestResponse>(new { Name = "request" }, "https://example.com/users");

        Assert.NotNull(result);
        Assert.Equal("posted", result!.Name);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public async Task SendRequest_ShouldAttachHeaders_AndSendJsonBody()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            captured = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json")
            };
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
    public async Task Put_ShouldUsePutMethod()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json")
            });
        });

        var service = CreateService(handler);

        var (result, error) = await service.Put<TestResponse>(new { Name = "put" }, "https://example.com/items/1");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, error);
        Assert.Equal(HttpMethod.Put, captured?.Method);
    }

    [Fact]
    public async Task Delete_ShouldUseDeleteMethod()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json")
            });
        });

        var service = CreateService(handler);

        var (result, error) = await service.Delete<TestResponse>("https://example.com/items/1");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, error);
        Assert.Equal(HttpMethod.Delete, captured?.Method);
    }

    [Fact]
    public async Task Patch_ShouldUsePatchMethod()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json")
            });
        });

        var service = CreateService(handler);

        var (result, error) = await service.Patch<TestResponse>(new { Name = "patch" }, "https://example.com/items/1");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, error);
        Assert.Equal(HttpMethod.Patch, captured?.Method);
    }

    [Fact]
    public async Task SendFormUrlEncoded_ShouldSendFormUrlEncodedContent()
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

        var (result, error) = await service.SendFormUrlEncoded<TestResponse>(
            HttpMethod.Put,
            new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
            "https://example.com/form");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, error);
        Assert.NotNull(captured?.Content);
        Assert.Equal("application/x-www-form-urlencoded", captured!.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SendRequest_ShouldUseFormUrlEncoded_WhenContentTypeIsFormUrlEncoded()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            captured = request;
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(handler);

        var (result, error) = await service.SendRequest<TestResponse>(
            HttpMethod.Post,
            "https://example.com/token",
            new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "api.read" },
            "application/x-www-form-urlencoded");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, error);
        Assert.Equal("application/x-www-form-urlencoded", captured!.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("grant_type=client_credentials", body);
        Assert.Contains("scope=api.read", body);
    }

    [Fact]
    public async Task SendRequest_ShouldReturnErrorBody_WhenStatusCodeIsNotSuccess()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("bad-request")
            }));

        var service = CreateService(handler);

        var (result, error) = await service.SendRequest<TestResponse>(HttpMethod.Get, "https://example.com/bad");

        Assert.Null(result);
        Assert.Equal("bad-request", error);
    }

    [Fact]
    public async Task SendRequest_ShouldReturnDeserializationError_WhenJsonIsInvalid()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-json", Encoding.UTF8, "application/json")
            }));

        var service = CreateService(handler);

        var (result, error) = await service.SendRequest<TestResponse>(HttpMethod.Get, "https://example.com/invalid-json");

        Assert.Null(result);
        Assert.Contains("Error deserializing response", error);
    }

    [Fact]
    public async Task SendRequest_ShouldReturnObject_WhenResponseIsEmptyAndTypeIsObject()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            }));

        var service = CreateService(handler);

        var (result, error) = await service.SendRequest<object>(HttpMethod.Get, "https://example.com/empty");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public async Task SendRequest_ShouldNotCreateContent_WhenContentTypeIsNull()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json")
            });
        });

        var service = CreateService(handler);

        var (result, error) = await service.SendRequest<TestResponse>(
            HttpMethod.Post,
            "https://example.com/no-content",
            payload: new { Value = "x" },
            contentType: null!);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, error);
        Assert.Null(captured!.Content);
    }

    [Fact]
    public async Task SendRequest_ShouldUseRawStringPayload_WhenPayloadIsString()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            captured = request;
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(handler);

        var (result, error) = await service.SendRequest<TestResponse>(
            HttpMethod.Post,
            "https://example.com/raw",
            payload: "plain-text",
            contentType: "text/plain");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, error);
        Assert.NotNull(captured?.Content);
        Assert.Equal("text/plain", captured!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("plain-text", body);
    }

    [Fact]
    public void RetryCallback_ShouldHandleMissingAndPresentUrlContext()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("fail")
            }));

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(activityListener);

        var service = CreateService(handler);
        var callback = typeof(HttpService).GetMethod("<.ctor>b__5_2", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(callback);

        var result = new DelegateResult<HttpResponseMessage>(new HttpResponseMessage(HttpStatusCode.BadRequest));

        var contextWithUrl = new Context { ["url"] = "https://example.com/retry" };
        var contextWithoutUrl = new Context();

        var ex1 = Record.Exception(() => callback!.Invoke(service, new object[] { result, TimeSpan.FromMilliseconds(1), 1, contextWithUrl }));
        var ex2 = Record.Exception(() => callback!.Invoke(service, new object[] { result, TimeSpan.FromMilliseconds(1), 2, contextWithoutUrl }));

        Assert.Null(ex1);
        Assert.NotNull(ex2);
        var tie = Assert.IsType<TargetInvocationException>(ex2);
        Assert.IsType<KeyNotFoundException>(tie.InnerException);
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
