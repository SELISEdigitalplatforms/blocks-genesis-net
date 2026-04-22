using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Blocks.Genesis;

public sealed class RequestMetricsMiddleware
{
    private static readonly Meter Meter = new("blocks.genesis.http", "1.0.0");
    private static readonly Histogram<double> RequestDurationMs = Meter.CreateHistogram<double>(
        "blocks.genesis.http.request.duration",
        unit: "ms",
        description: "HTTP request duration in milliseconds");

    private readonly RequestDelegate _next;

    public RequestMetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        await _next(context);
        stopwatch.Stop();

        var route = context.GetEndpoint()?.DisplayName ?? context.Request.Path.Value ?? string.Empty;
        RequestDurationMs.Record(
            stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("route", route),
            new KeyValuePair<string, object?>("method", context.Request.Method),
            new KeyValuePair<string, object?>("status_code", context.Response.StatusCode));
    }
}
