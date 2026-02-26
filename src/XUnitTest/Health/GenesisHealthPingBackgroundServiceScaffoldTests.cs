using Blocks.Genesis.Health;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Health;

public class GenesisHealthPingBackgroundServiceScaffoldTests
{
    [Fact]
    public void Type_ShouldBeAccessible()
    {
        Assert.NotNull(typeof(GenesisHealthPingBackgroundService));
    }

    [Theory]
    [InlineData(60, 0, 60)]
    [InlineData(0, 0, 60)]
    [InlineData(60, 1, 120)]
    [InlineData(60, 2, 240)]
    [InlineData(120, 10, 300)]
    public void CalculateDelay_ShouldReturnExpectedSeconds(int interval, int failures, int expectedSeconds)
    {
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("CalculateDelay", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var delay = (TimeSpan)method!.Invoke(null, [interval, failures])!;

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Theory]
    [InlineData(null, "[empty]")]
    [InlineData("", "[empty]")]
    [InlineData("not-a-url", "[masked]")]
    public void MaskUrl_ShouldHandleEmptyAndInvalidValues(string? url, string expected)
    {
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("MaskUrl", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, [url])!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MaskUrl_ShouldReturnMaskedHostAndSuffix_ForValidUrl()
    {
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("MaskUrl", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, ["https://api.example.com/secret/path/token12345"])!;

        Assert.StartsWith("https://api.example.com/***", result);
    }

    [Fact]
    public async Task PingAsync_ShouldReturnTrue_OnSuccessStatusCode()
    {
        var service = CreateServiceForPing(_ =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("PingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var cfg = new BlocksServicesHealthConfiguration { Endpoint = "https://api.example.com/health", HealthCheckEnabled = true };
        var task = (Task<bool>)method!.Invoke(service, [cfg, CancellationToken.None])!;
        var result = await task;

        Assert.True(result);
    }

    [Fact]
    public async Task PingAsync_ShouldReturnFalse_OnServerErrorStatusCode()
    {
        var service = CreateServiceForPing(_ =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("PingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var cfg = new BlocksServicesHealthConfiguration { Endpoint = "https://api.example.com/health", HealthCheckEnabled = true };
        var task = (Task<bool>)method!.Invoke(service, [cfg, CancellationToken.None])!;
        var result = await task;

        Assert.False(result);
    }

    [Fact]
    public void ApplyConfig_ShouldUpdateCurrentConfig()
    {
        var service = CreateServiceForPing(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        var apply = typeof(GenesisHealthPingBackgroundService).GetMethod("ApplyConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);

        var config = new BlocksServicesHealthConfiguration
        {
            ServiceName = "svc",
            Endpoint = "https://api.example.com/health",
            HealthCheckEnabled = true,
            PingIntervalSeconds = 30
        };

        apply!.Invoke(service, [config]);

        var currentField = typeof(GenesisHealthPingBackgroundService).GetField("_currentConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        var current = (BlocksServicesHealthConfiguration?)currentField!.GetValue(service);
        Assert.NotNull(current);
        Assert.Equal("svc", current!.ServiceName);
    }

    [Fact]
    public async Task RefreshConfigurationAsync_ShouldApplyConfig_FromCache()
    {
        var service = CreateServiceForPing(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        var cfg = new BlocksServicesHealthConfiguration
        {
            ServiceName = "svc-test",
            Endpoint = "https://api.example.com/health",
            HealthCheckEnabled = true,
            PingIntervalSeconds = 45
        };

        var cache = new Mock<IDatabase>();
        cache.Setup(c => c.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)System.Text.Json.JsonSerializer.Serialize(cfg));

        SetField(service, "_cacheDb", cache.Object);

        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("RefreshConfigurationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;

        var currentField = typeof(GenesisHealthPingBackgroundService).GetField("_currentConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        var current = (BlocksServicesHealthConfiguration?)currentField!.GetValue(service);

        Assert.NotNull(current);
        Assert.Equal(45, current!.PingIntervalSeconds);
    }

    [Fact]
    public async Task PingAsync_ShouldReturnFalse_OnHttpRequestException()
    {
        var service = CreateServiceForPing(_ => throw new HttpRequestException("network"));
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("PingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var cfg = new BlocksServicesHealthConfiguration { Endpoint = "https://api.example.com/health", HealthCheckEnabled = true };
        var task = (Task<bool>)method!.Invoke(service, [cfg, CancellationToken.None])!;
        var result = await task;

        Assert.False(result);
    }

    [Fact]
    public async Task PingAsync_ShouldReturnFalse_OnTaskCanceledException()
    {
        var service = CreateServiceForPing(_ => throw new TaskCanceledException("timeout"));
        var method = typeof(GenesisHealthPingBackgroundService).GetMethod("PingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var cfg = new BlocksServicesHealthConfiguration { Endpoint = "https://api.example.com/health", HealthCheckEnabled = true };
        var task = (Task<bool>)method!.Invoke(service, [cfg, CancellationToken.None])!;
        var result = await task;

        Assert.False(result);
    }

    private static GenesisHealthPingBackgroundService CreateServiceForPing(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var service = (GenesisHealthPingBackgroundService)RuntimeHelpers.GetUninitializedObject(typeof(GenesisHealthPingBackgroundService));

        var logger = new Mock<ILogger<GenesisHealthPingBackgroundService>>();
        SetField(service, "_logger", logger.Object);
        SetField(service, "_serviceName", "svc-test");
        SetField(service, "_httpClient", new HttpClient(new StubHandler(responder)));

        return service;
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request);
    }
}
