using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace Blocks.Genesis.Health;

public static class HealthCheckResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                pair => pair.Key,
                pair => new
                {
                    status = pair.Value.Status.ToString(),
                    duration = pair.Value.Duration.TotalMilliseconds,
                    description = pair.Value.Description
                })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
