using Blocks.Genesis;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Moq;
using System.Diagnostics.Metrics;

namespace XUnitTest.Lmt;

public class MongoDBMetricsExporterScaffoldTests
{
    [Fact]
    public void Type_ShouldBeAccessible()
    {
        Assert.NotNull(typeof(MongoDBMetricsExporter));
    }

    [Fact]
    public void Constructor_ShouldCreateExporterInstance_WithValidDependencies()
    {
        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(x => x.MetricConnectionString).Returns("mongodb://localhost:27017");

        var exporter = new MongoDBMetricsExporter("svc-metrics", blocksSecret.Object);

        Assert.NotNull(exporter);
    }

    [Fact]
    public void Type_ShouldInheritBaseExporterOfMetric()
    {
        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(x => x.MetricConnectionString).Returns("mongodb://localhost:27017");

        var exporter = new MongoDBMetricsExporter("svc-metrics", blocksSecret.Object);

        Assert.IsAssignableFrom<BaseExporter<Metric>>(exporter);
    }

    [Fact]
    public void Export_ShouldReturnSuccess_ForEmptyBatch()
    {
        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(x => x.MetricConnectionString).Returns("mongodb://localhost:27017");

        var exporter = new MongoDBMetricsExporter("svc-metrics", blocksSecret.Object);
        var emptyBatch = default(Batch<Metric>);

        var result = exporter.Export(in emptyBatch);

        Assert.Equal(ExportResult.Success, result);
    }

    [Fact]
    public void Export_ShouldHandleMultipleMetricTypes_ThroughMeterProvider()
    {
        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(x => x.MetricConnectionString).Returns("mongodb://localhost:27017");

        var exporter = new MongoDBMetricsExporter("svc-metrics", blocksSecret.Object);

        using var meter = new Meter("test-meter");
        var counterLong = meter.CreateCounter<long>("counter-long");
        var counterDouble = meter.CreateCounter<double>("counter-double");
        var histogram = meter.CreateHistogram<double>("histogram-double");
        meter.CreateObservableGauge<long>("gauge-long", () => 7);
        meter.CreateObservableGauge<double>("gauge-double", () => 2.5);

        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("test-meter")
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 50))
            .Build();

        counterLong.Add(3);
        counterDouble.Add(1.2);
        histogram.Record(4.2);

        var flushed = provider.ForceFlush();
        Assert.True(flushed);
    }
}
