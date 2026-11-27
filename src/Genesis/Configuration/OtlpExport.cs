using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Sinks.OpenTelemetry;
using OpenTelemetry.Trace;

namespace Blocks.Genesis;

/// <summary>
/// Switches OTLP export of traces, metrics and logs on from the standard OpenTelemetry environment
/// variables. Nothing is registered unless OTEL_EXPORTER_OTLP_ENDPOINT is set.
/// </summary>
internal static class OtlpExport
{
    internal const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    internal const string SamplerVariable = "OTEL_TRACES_SAMPLER";

    internal const string LogsEndpointVariable = "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT";
    internal const string ProtocolVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";

    internal const string MongoExportVariable = "Lmt__MongoExport";

    /// <summary>
    /// The MongoDB trace processor and log sink are on unless Lmt__MongoExport is "false" (I03-WP3). When off, the OS console's
    /// log and trace screens are empty.
    /// </summary>
    internal static bool MongoExportEnabled(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable(MongoExportVariable);
        return !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsEnabled() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EndpointVariable));

    internal static bool UseDefaultSampler() => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SamplerVariable));

    internal static TracerProviderBuilder AddOtlpExporterWhenConfigured(this TracerProviderBuilder builder)
        => IsEnabled() ? builder.AddOtlpExporter() : builder;

    internal static MeterProviderBuilder AddOtlpExporterWhenConfigured(this MeterProviderBuilder builder)
        => IsEnabled() ? builder.AddOtlpExporter() : builder;

    /// <summary>
    /// The URL the log sink posts to. A logs-specific endpoint is used as it is. The general endpoint is used as it is for gRPC
    /// (the default protocol) and gets /v1/logs appended for http/protobuf, as the OpenTelemetry specification says.
    /// </summary>
    internal static string ResolveLogsEndpoint(string? generalEndpoint, string? logsEndpoint, string? protocol)
    {
        if (!string.IsNullOrWhiteSpace(logsEndpoint)) return logsEndpoint.Trim();
        var endpoint = (generalEndpoint ?? string.Empty).Trim();
        return IsHttpProtobuf(protocol) ? endpoint.TrimEnd('/') + "/v1/logs" : endpoint;
    }

    internal static bool IsHttpProtobuf(string? protocol)
        => string.Equals(protocol?.Trim(), "http/protobuf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds the Serilog OpenTelemetry sink when OTEL_EXPORTER_OTLP_ENDPOINT is set. Trace id and span id go out as the log record's
    /// own fields, and every event property (TenantId, TraceId, SpanId and so on) goes out as an attribute.
    /// </summary>
    internal static LoggerConfiguration WriteToOtlpWhenConfigured(this LoggerConfiguration configuration, string serviceName)
    {
        if (!IsEnabled()) return configuration;
        var protocol = Environment.GetEnvironmentVariable(ProtocolVariable);
        var endpoint = ResolveLogsEndpoint(
            Environment.GetEnvironmentVariable(EndpointVariable),
            Environment.GetEnvironmentVariable(LogsEndpointVariable),
            protocol);
        return configuration.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = endpoint;
            options.Protocol = IsHttpProtobuf(protocol) ? OtlpProtocol.HttpProtobuf : OtlpProtocol.Grpc;
            options.IncludedData = IncludedData.TraceIdField | IncludedData.SpanIdField | IncludedData.SpecRequiredResourceAttributes;
            options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = serviceName };
        });
    }
}
