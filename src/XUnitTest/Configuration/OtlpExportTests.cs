using Blocks.Genesis;
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
}
