using Blocks.Genesis.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace XUnitTest.Health;

public class HealthCheckResponseWriterTests
{
    [Fact]
    public async Task WriteAsync_ShouldSerializeReport_WithStatusAndEntries()
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["mongo"] = new HealthReportEntry(
                HealthStatus.Healthy,
                "mongo ok",
                TimeSpan.FromMilliseconds(42),
                exception: null,
                data: null),
            ["redis"] = new HealthReportEntry(
                HealthStatus.Degraded,
                "redis slow",
                TimeSpan.FromMilliseconds(120),
                exception: null,
                data: null)
        };
        var report = new HealthReport(entries, HealthStatus.Degraded, TimeSpan.FromMilliseconds(200));

        await HealthCheckResponseWriter.WriteAsync(context, report);

        Assert.Equal("application/json", context.Response.ContentType);

        body.Position = 0;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("Degraded", root.GetProperty("status").GetString());
        Assert.Equal(200, root.GetProperty("totalDuration").GetDouble());

        var checks = root.GetProperty("checks");
        Assert.Equal("Healthy", checks.GetProperty("mongo").GetProperty("status").GetString());
        Assert.Equal(42, checks.GetProperty("mongo").GetProperty("duration").GetDouble());
        Assert.Equal("mongo ok", checks.GetProperty("mongo").GetProperty("description").GetString());

        Assert.Equal("Degraded", checks.GetProperty("redis").GetProperty("status").GetString());
        Assert.Equal("redis slow", checks.GetProperty("redis").GetProperty("description").GetString());
    }

    [Fact]
    public async Task WriteAsync_ShouldHandleEmptyEntries()
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Healthy,
            TimeSpan.Zero);

        await HealthCheckResponseWriter.WriteAsync(context, report);

        body.Position = 0;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("totalDuration").GetDouble());
        Assert.Equal(0, root.GetProperty("checks").EnumerateObject().Count());
    }

    [Fact]
    public async Task WriteAsync_ShouldWriteNullDescription_WhenEntryHasNoDescription()
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["self"] = new HealthReportEntry(
                HealthStatus.Unhealthy,
                description: null,
                TimeSpan.FromMilliseconds(5),
                exception: null,
                data: null)
        };
        var report = new HealthReport(entries, HealthStatus.Unhealthy, TimeSpan.FromMilliseconds(5));

        await HealthCheckResponseWriter.WriteAsync(context, report);

        body.Position = 0;
        using var doc = JsonDocument.Parse(body);
        var selfCheck = doc.RootElement.GetProperty("checks").GetProperty("self");

        Assert.Equal("Unhealthy", selfCheck.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, selfCheck.GetProperty("description").ValueKind);
    }
}
