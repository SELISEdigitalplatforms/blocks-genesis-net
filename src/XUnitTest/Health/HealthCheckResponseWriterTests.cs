using Blocks.Genesis.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace XUnitTest.Health;

/// <summary>Tests for <see cref="HealthCheckResponseWriter"/>, which serialises a health report as JSON.</summary>
public class HealthCheckResponseWriterTests
{
    [Fact]
    public async Task WriteAsync_ShouldWriteJsonReport_WithStatusAndEntries()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["mongodb"] = new HealthReportEntry(HealthStatus.Healthy, "reachable", TimeSpan.FromMilliseconds(5), exception: null, data: null),
            ["redis"] = new HealthReportEntry(HealthStatus.Degraded, "slow", TimeSpan.FromMilliseconds(9), exception: null, data: null),
        };
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(20));

        await HealthCheckResponseWriter.WriteAsync(context, report);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Contains("\"status\":\"Degraded\"", body);
        Assert.Contains("mongodb", body);
        Assert.Contains("redis", body);
        Assert.Contains("reachable", body);
    }
}
