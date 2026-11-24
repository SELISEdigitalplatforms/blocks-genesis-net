using Blocks.Genesis;
using Serilog;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace XUnitTest.Configuration;

[Collection("DirectorySensitiveTests")]
public class OtlpExportTests
{
    private static void WithEnv(string name, string? value, Action body)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        try { Environment.SetEnvironmentVariable(name, value); body(); }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    private static bool HasOtlpOptions(IServiceCollection services)
        => services.Any(d => d.ServiceType.ToString().Contains(nameof(OtlpExporterOptions)));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("http://otel-lgtm:4317", true)]
    public void IsEnabled_FollowsEndpointVariable(string? value, bool expected)
        => WithEnv(OtlpExport.EndpointVariable, value, () => Assert.Equal(expected, OtlpExport.IsEnabled()));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("traceidratio", false)]
    public void UseDefaultSampler_OnlyWhenSamplerVariableUnset(string? value, bool expected)
        => WithEnv(OtlpExport.SamplerVariable, value, () => Assert.Equal(expected, OtlpExport.UseDefaultSampler()));

    [Fact]
    public void TracesAndMetrics_RegisterNoExporter_WhenEndpointUnset()
        => WithEnv(OtlpExport.EndpointVariable, null, () =>
        {
            var services = new ServiceCollection();
            services.AddOpenTelemetry()
                .WithTracing(b => b.AddOtlpExporterWhenConfigured())
                .WithMetrics(b => b.AddOtlpExporterWhenConfigured());
            Assert.False(HasOtlpOptions(services));
        });

    [Fact]
    public void Traces_RegisterExporter_WhenEndpointSet()
        => WithEnv(OtlpExport.EndpointVariable, "http://localhost:4317", () =>
        {
            var services = new ServiceCollection();
            services.AddOpenTelemetry().WithTracing(b => b.AddOtlpExporterWhenConfigured());
            Assert.True(HasOtlpOptions(services));
        });

    [Fact]
    public void Metrics_RegisterExporter_WhenEndpointSet()
        => WithEnv(OtlpExport.EndpointVariable, "http://localhost:4317", () =>
        {
            var services = new ServiceCollection();
            services.AddOpenTelemetry().WithMetrics(b => b.AddOtlpExporterWhenConfigured());
            Assert.True(HasOtlpOptions(services));
        });

    [Theory]
    [InlineData("http://otel:4317", null, null, "http://otel:4317")]
    [InlineData("http://otel:4317", null, "grpc", "http://otel:4317")]
    [InlineData("http://otel:4318", null, "http/protobuf", "http://otel:4318/v1/logs")]
    [InlineData("http://otel:4318/", null, "HTTP/Protobuf", "http://otel:4318/v1/logs")]
    [InlineData("http://otel:4318", "http://logs:9/custom", "http/protobuf", "http://logs:9/custom")]
    public void ResolveLogsEndpoint_FollowsProtocolAndLogsVariable(string general, string? logs, string? protocol, string expected)
        => Assert.Equal(expected, OtlpExport.ResolveLogsEndpoint(general, logs, protocol));

    [Fact]
    public void LogSink_IsNotAdded_WhenEndpointUnset()
        => WithEnv(OtlpExport.EndpointVariable, null, () =>
        {
            var configuration = new LoggerConfiguration();
            Assert.Same(configuration, configuration.WriteToOtlpWhenConfigured("svc"));
        });

    [Fact]
    public void LogSink_SendsTenantIdAndTraceIds_ToTheLogsEndpoint()
    {
        var port = GetFreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            context.Response.StatusCode = 200;
            context.Response.Close();
            return (Path: context.Request.Url!.AbsolutePath, Body: buffer.ToArray());
        });

        var previousProtocol = Environment.GetEnvironmentVariable(OtlpExport.ProtocolVariable);
        Environment.SetEnvironmentVariable(OtlpExport.ProtocolVariable, "http/protobuf");
        try
        {
            WithEnv(OtlpExport.EndpointVariable, $"http://127.0.0.1:{port}", () =>
            {
                using var activity = new Activity("otlp-log-test").Start();
                var logger = new LoggerConfiguration().WriteToOtlpWhenConfigured("otlp-log-test-service").CreateLogger();
                logger.Information("hello {TenantId}", "tenant-under-test");
                ((IDisposable)logger).Dispose();

                Assert.True(received.Wait(TimeSpan.FromSeconds(20)), "the logs endpoint received no request");
                var (path, body) = received.Result;
                var text = Encoding.UTF8.GetString(body);
                Assert.Equal("/v1/logs", path);
                Assert.Contains("tenant-under-test", text);
                Assert.Contains("otlp-log-test-service", text);
                Assert.True(IndexOf(body, Convert.FromHexString(activity.TraceId.ToHexString())) >= 0, "the trace id is not in the log record");
                Assert.True(IndexOf(body, Convert.FromHexString(activity.SpanId.ToHexString())) >= 0, "the span id is not in the log record");
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(OtlpExport.ProtocolVariable, previousProtocol);
            listener.Stop();
        }
    }

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
