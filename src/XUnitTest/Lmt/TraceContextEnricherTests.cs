using Blocks.Genesis;
using OpenTelemetry;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Core;
using System.Diagnostics;

namespace XUnitTest.Lmt;

public class TraceContextEnricherTests
{
    [Fact]
    public void Enrich_ShouldNotAddProperties_WhenNoActivity()
    {
        Activity.Current = null;
        var enricher = new TraceContextEnricher();
        var logEvent = CreateLogEvent();

        enricher.Enrich(logEvent, new TestLogEventPropertyFactory());

        Assert.Empty(logEvent.Properties);
    }

    [Fact]
    public void Enrich_ShouldAddTenantAndTraceProperties_WhenActivityExists()
    {
        using var source = new ActivitySource("test-trace-enricher");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-trace-enricher",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("op");
        Assert.NotNull(activity);

        Baggage.SetBaggage("TenantId", "tenant-123");
        try
        {
            var enricher = new TraceContextEnricher();
            var logEvent = CreateLogEvent();

            enricher.Enrich(logEvent, new TestLogEventPropertyFactory());

            Assert.Equal("tenant-123", ((ScalarValue)logEvent.Properties["TenantId"]).Value?.ToString());
            Assert.NotNull(logEvent.Properties["TraceId"]);
            Assert.NotNull(logEvent.Properties["SpanId"]);
        }
        finally
        {
            Baggage.SetBaggage("TenantId", null);
        }
    }

    [Fact]
    public void Enrich_ShouldFallbackTenantToMiscellaneous_WhenBaggageMissing()
    {
        using var source = new ActivitySource("test-trace-enricher-fallback");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-trace-enricher-fallback",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("op");
        Assert.NotNull(activity);

        Baggage.SetBaggage("TenantId", null);

        var enricher = new TraceContextEnricher();
        var logEvent = CreateLogEvent();

        enricher.Enrich(logEvent, new TestLogEventPropertyFactory());

        Assert.Equal(BlocksConstants.Miscellaneous, ((ScalarValue)logEvent.Properties["TenantId"]).Value?.ToString());
    }

    private static LogEvent CreateLogEvent()
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("test"), new List<LogEventProperty>());
    }

    private sealed class TestLogEventPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
