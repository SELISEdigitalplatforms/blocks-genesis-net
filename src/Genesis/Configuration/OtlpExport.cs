using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Blocks.Genesis;

/// <summary>
/// Switches OTLP export of traces and metrics on from the standard OpenTelemetry environment
/// variables. Nothing is registered unless OTEL_EXPORTER_OTLP_ENDPOINT is set.
/// </summary>
internal static class OtlpExport
{
    internal const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    internal const string SamplerVariable = "OTEL_TRACES_SAMPLER";

    internal static bool IsEnabled() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EndpointVariable));

    internal static bool UseDefaultSampler() => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SamplerVariable));

    internal static TracerProviderBuilder AddOtlpExporterWhenConfigured(this TracerProviderBuilder builder)
        => IsEnabled() ? builder.AddOtlpExporter() : builder;

    internal static MeterProviderBuilder AddOtlpExporterWhenConfigured(this MeterProviderBuilder builder)
        => IsEnabled() ? builder.AddOtlpExporter() : builder;
}
